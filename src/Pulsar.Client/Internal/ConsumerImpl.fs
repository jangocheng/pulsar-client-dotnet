﻿namespace Pulsar.Client.Api

open FSharp.Control.Tasks.V2.ContextInsensitive
open System.Threading.Tasks
open FSharp.UMX
open System.Collections.Generic
open System
open Pulsar.Client.Internal
open Pulsar.Client.Common
open Microsoft.Extensions.Logging
open System.IO
open ProtoBuf
open Pulsar.Client.Schema
open pulsar.proto
open System.Threading
open System.Timers
    
type internal ParseResult<'T> =
    | ParseOk of struct(byte[]*'T)
    | ParseError of CommandAck.ValidationError
    
type internal ConsumerMessage<'T> =
    | ConnectionOpened
    | ConnectionFailed of exn
    | ConnectionClosed of ClientCnx
    | ReachedEndOfTheTopic
    | MessageReceived of RawMessage * ClientCnx
    | Receive of AsyncReplyChannel<ResultOrException<Message<'T>>>
    | BatchReceive of AsyncReplyChannel<ResultOrException<Messages<'T>>>
    | SendBatchByTimeout
    | Acknowledge of MessageId * AckType
    | NegativeAcknowledge of MessageId
    | RedeliverUnacknowledged of RedeliverSet * AsyncReplyChannel<unit>
    | RedeliverAllUnacknowledged of AsyncReplyChannel<unit>
    | SeekAsync of SeekData * AsyncReplyChannel<ResultOrException<unit>>
    | SendFlowPermits of int
    | HasMessageAvailable of AsyncReplyChannel<Task<bool>>
    | ActiveConsumerChanged of bool
    | Close of AsyncReplyChannel<ResultOrException<unit>>
    | Unsubscribe of AsyncReplyChannel<ResultOrException<unit>>
    | StatTick
    | GetStats of AsyncReplyChannel<ConsumerStats>

type internal ConsumerImpl<'T> (consumerConfig: ConsumerConfiguration<'T>, clientConfig: PulsarClientConfiguration, connectionPool: ConnectionPool,
                           partitionIndex: int, startMessageId: MessageId option, lookup: BinaryLookupService,
                           startMessageRollbackDuration: TimeSpan, createTopicIfDoesNotExist: bool, schema: ISchema<'T>,
                           schemaProvider: MultiVersionSchemaInfoProvider option,
                           interceptors: ConsumerInterceptors<'T>, cleanup: ConsumerImpl<'T> -> unit) as this =

    [<Literal>]
    let MAX_REDELIVER_UNACKNOWLEDGED = 1000
    let consumerId = Generators.getNextConsumerId()
    let incomingMessages = Queue<Message<'T>>()
    let waiters = Queue<AsyncReplyChannel<ResultOrException<Message<'T>>>>()
    let batchWaiters = Queue<CancellationTokenSource*AsyncReplyChannel<ResultOrException<Messages<'T>>>>()
    let subscribeTsc = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let prefix = sprintf "consumer(%u, %s, %i)" %consumerId consumerConfig.ConsumerName partitionIndex
    let subscribeTimeout = DateTime.Now.Add(clientConfig.OperationTimeout)
    let mutable hasReachedEndOfTopic = false
    let mutable avalablePermits = 0
    let mutable startMessageId = startMessageId
    let mutable lastMessageIdInBroker = MessageId.Earliest
    let mutable lastDequeuedMessageId = MessageId.Earliest
    let mutable duringSeek = None
    let initialStartMessageId = startMessageId
    let mutable incomingMessagesSize = 0L
    let receiverQueueRefillThreshold = consumerConfig.ReceiverQueueSize / 2
    let deadLettersProcessor = consumerConfig.DeadLettersProcessor
    let isDurable = consumerConfig.SubscriptionMode = SubscriptionMode.Durable
    let stats =
        if clientConfig.StatsInterval = TimeSpan.Zero then
            ConsumerStatsImpl.CONSUMER_STATS_DISABLED
        else
            ConsumerStatsImpl(prefix) :> IConsumerStatsRecorder
    
    let statTimer = new Timer()
    let startStatTimer () =
        if clientConfig.StatsInterval <> TimeSpan.Zero then
            statTimer.Interval <- clientConfig.StatsInterval.TotalMilliseconds
            statTimer.AutoReset <- true
            statTimer.Elapsed.Add(fun _ -> this.Mb.Post(StatTick))
            statTimer.Start()
    
    let keyValueProcessor = KeyValueProcessor.GetInstance schema

    let wrapPostAndReply (mbAsyncReply: Async<ResultOrException<'A>>) =
        async {
            match! mbAsyncReply with
            | Ok msg ->
                return msg
            | Error ex ->
                return reraize ex
        }
    
    let connectionHandler =
        ConnectionHandler(prefix,
                          connectionPool,
                          lookup,
                          consumerConfig.Topic.CompleteTopicName,
                          (fun _ -> this.Mb.Post(ConsumerMessage.ConnectionOpened)),
                          (fun ex -> this.Mb.Post(ConsumerMessage.ConnectionFailed ex)),
                          Backoff({ BackoffConfig.Default with Initial = TimeSpan.FromMilliseconds(100.0); Max = TimeSpan.FromSeconds(60.0) }))

    let hasMoreMessages (lastMessageIdInBroker: MessageId) (lastDequeuedMessage: MessageId) (inclusive: bool) =
        if (inclusive && lastMessageIdInBroker >= lastDequeuedMessage && lastMessageIdInBroker.EntryId <> %(-1L)) then
            true
        elif (not inclusive && lastMessageIdInBroker > lastDequeuedMessage && lastMessageIdInBroker.EntryId <> %(-1L)) then
            true
        else
            false

    let increaseAvailablePermits delta =
        avalablePermits <- avalablePermits + delta
        if avalablePermits >= receiverQueueRefillThreshold then
            this.Mb.Post(ConsumerMessage.SendFlowPermits avalablePermits)
            avalablePermits <- 0

    /// Clear the internal receiver queue and returns the message id of what was the 1st message in the queue that was not seen by the application
    let clearReceiverQueue() =
        let nextMsg =
            if incomingMessages.Count > 0 then
                let nextMessageInQueue = incomingMessages.Dequeue().MessageId
                incomingMessagesSize <- 0L
                incomingMessages.Clear()
                Some nextMessageInQueue
            else
                None
        match duringSeek with
        | Some _ as seekMsgId ->
            duringSeek <- None
            seekMsgId
        | None when isDurable ->
            startMessageId
        | _  ->        
            match nextMsg with
            | Some nextMessageInQueue ->                
                let previousMessage =
                    match nextMessageInQueue.Type with
                    | Cumulative (index, acker) ->
                        // Get on the previous message within the current batch
                        { nextMessageInQueue with Type = Cumulative(index - %1, acker) }
                    | Individual ->
                        // Get on previous message in previous entry
                        { nextMessageInQueue with EntryId = nextMessageInQueue.EntryId - %1L }
                Some previousMessage
            | None ->
                if lastDequeuedMessageId <> MessageId.Earliest then
                    // If the queue was empty we need to restart from the message just after the last one that has been dequeued
                    // in the past
                    Some lastDequeuedMessageId
                else
                    // No message was received or dequeued by this consumer. Next message would still be the startMessageId
                    startMessageId
    
    let getLastMessageIdAsync() =
        
        let rec internalGetLastMessageIdAsync(backoff: Backoff, remainingTimeMs: int) =
            async {
                match connectionHandler.ConnectionState with
                | Ready clientCnx ->                    
                    let requestId = Generators.getNextRequestId()
                    let payload = Commands.newGetLastMessageId consumerId requestId
                    try
                        let! response = clientCnx.SendAndWaitForReply requestId payload |> Async.AwaitTask
                        let lastMessageId = response |> PulsarResponseType.GetLastMessageId
                        return lastMessageId
                    with
                    | ex ->
                        Log.Logger.LogError(ex, "{0} failed getLastMessageId", prefix)
                        return reraize ex
                | _ ->
                    let nextDelay = Math.Min(backoff.Next(), remainingTimeMs)
                    if nextDelay <= 0 then
                        return
                            "Couldn't get the last message id withing configured timeout"
                            |> TimeoutException
                            |> raise
                    else
                        Log.Logger.LogWarning("Could not get connection while GetLastMessageId -- Will try again in {0} ms", nextDelay)    
                        do! Async.Sleep nextDelay                    
                        return! internalGetLastMessageIdAsync(backoff, remainingTimeMs - nextDelay)
            }
        
        match connectionHandler.ConnectionState with
        | Closing | Closed ->
            "Consumer is already closed"
            |> AlreadyClosedException
            |> Task.FromException<MessageId>
        | _ ->
            let backoff = Backoff { BackoffConfig.Default with
                                        Initial = TimeSpan.FromMilliseconds(100.0)
                                        Max = (clientConfig.OperationTimeout + clientConfig.OperationTimeout)}
            internalGetLastMessageIdAsync(backoff, int clientConfig.OperationTimeout.TotalMilliseconds) |> Async.StartAsTask
    
    let redeliverMessages messages =
        async {
            do! this.Mb.PostAndAsyncReply(fun channel -> RedeliverUnacknowledged (messages, channel))
        } |> Async.StartImmediate

    let unAckedMessageRedeliver messages =
        interceptors.OnAckTimeoutSend(this, messages)
        redeliverMessages messages        

    let negativeAcksRedeliver messages =
        interceptors.OnNegativeAcksSend(this, messages)
        redeliverMessages messages        
    
    let unAckedMessageTracker =
        if consumerConfig.AckTimeout > TimeSpan.Zero then
            if consumerConfig.AckTimeoutTickTime > TimeSpan.Zero then
                let tickDuration = if consumerConfig.AckTimeout > consumerConfig.AckTimeoutTickTime then consumerConfig.AckTimeoutTickTime else consumerConfig.AckTimeout
                UnAckedMessageTracker(prefix, consumerConfig.AckTimeout, tickDuration, unAckedMessageRedeliver) :> IUnAckedMessageTracker
            else
                UnAckedMessageTracker(prefix, consumerConfig.AckTimeout, consumerConfig.AckTimeout, unAckedMessageRedeliver) :> IUnAckedMessageTracker
        else
            UnAckedMessageTracker.UNACKED_MESSAGE_TRACKER_DISABLED

    let negativeAcksTracker = NegativeAcksTracker(prefix, consumerConfig.NegativeAckRedeliveryDelay, negativeAcksRedeliver)

    let getConnectionState() = connectionHandler.ConnectionState
    let sendAckPayload (cnx: ClientCnx) payload = cnx.Send payload

    let acksGroupingTracker =
        if consumerConfig.Topic.IsPersistent then
            AcknowledgmentsGroupingTracker(prefix, consumerId, consumerConfig.AcknowledgementsGroupTime, getConnectionState, sendAckPayload) :> IAcknowledgmentsGroupingTracker
        else
            AcknowledgmentsGroupingTracker.NonPersistentAcknowledgmentGroupingTracker

    let sendAcknowledge (messageId: MessageId) ackType =
        async {
            match ackType with
            | AckType.Individual ->
                match messageId.Type with
                | Individual ->
                    unAckedMessageTracker.Remove(messageId) |> ignore
                    stats.IncrementNumAcksSent(1)
                | Cumulative (_, batch) ->
                    unAckedMessageTracker.Remove(messageId) |> ignore
                    stats.IncrementNumAcksSent(batch.GetBatchSize())
                interceptors.OnAcknowledge(this, messageId, null)
            | AckType.Cumulative ->
                interceptors.OnAcknowledgeCumulative(this, messageId, null)
                let count = unAckedMessageTracker.RemoveMessagesTill(messageId)
                stats.IncrementNumAcksSent(count)
            do! acksGroupingTracker.AddAcknowledgment(messageId, ackType)
            // Consumer acknowledgment operation immediately succeeds. In any case, if we're not able to send ack to broker,
            // the messages will be re-delivered
            deadLettersProcessor.RemoveMessage messageId
        }

    let markAckForBatchMessage (msgId: MessageId) ackType (batchDetails: BatchDetails) =
        let (batchIndex, batchAcker) = batchDetails
        let isAllMsgsAcked =
            match ackType with
            | AckType.Individual ->
                batchAcker.AckIndividual(batchIndex)
            | AckType.Cumulative ->
                batchAcker.AckGroup(batchIndex)
        let outstandingAcks = batchAcker.GetOutstandingAcks()
        let batchSize = batchAcker.GetBatchSize()
        if isAllMsgsAcked then
            Log.Logger.LogDebug("{0} can ack message acktype {1}, cardinality {2}, length {3}",
                prefix, ackType, outstandingAcks, batchSize)
            true
        else
            match ackType with
            | AckType.Cumulative ->
                if not batchAcker.PrevBatchCumulativelyAcked then
                    sendAcknowledge msgId.PrevBatchMessageId ackType |> Async.StartImmediate
                    Log.Logger.LogDebug("{0} update PrevBatchCumulativelyAcked", prefix)
                    batchAcker.PrevBatchCumulativelyAcked <- true
                interceptors.OnAcknowledgeCumulative(this, msgId, null)
            | AckType.Individual ->
                interceptors.OnAcknowledge(this, msgId, null)
            Log.Logger.LogDebug("{0} cannot ack message acktype {1}, cardinality {2}, length {3}",
                prefix, ackType, outstandingAcks, batchSize)
            false

    let trySendAcknowledge ackType messageId =
        async {
            match messageId.Type with
            | Cumulative batchDetails when not (markAckForBatchMessage messageId ackType batchDetails) ->
                // other messages in batch are still pending ack.
                ()
            | _ ->
                do! sendAcknowledge messageId ackType
                Log.Logger.LogDebug("{0} acknowledged message - {1}, acktype {2}", prefix, messageId, ackType)
        }

    let trySendIndividualAcknowledge = trySendAcknowledge AckType.Individual

    let isPriorEntryIndex idx =
        match startMessageId with
        | None -> false
        | Some startMsgId ->
            if consumerConfig.ResetIncludeHead then
                idx < startMsgId.EntryId
            else
                idx <= startMsgId.EntryId

    let isPriorBatchIndex idx =
        match startMessageId with
        | None -> false
        | Some startMsgId ->
            match startMsgId.Type with
            | Individual -> false
            | Cumulative (batchIndex, _) ->
                if consumerConfig.ResetIncludeHead then
                    idx < batchIndex
                else
                    idx <= batchIndex

    let isSameEntry (msgId: MessageId) =
        match startMessageId with
        | None ->
            false
        | Some startMsgId ->
                startMsgId.LedgerId = msgId.LedgerId
                && startMsgId.EntryId = msgId.EntryId
    
    let getSchemaVersionBytes =
        Option.map (fun (SchemaVersion bytes) -> bytes) >> Option.defaultValue null
    
    let clearDeadLetters() = deadLettersProcessor.ClearMessages()

    let getNewIndividualMsgIdWithPartition messageId =
        { messageId with Type = Individual; Partition = partitionIndex; TopicName = %"" }

    let processDeadLetters (messageId : MessageId) =
        task {
            let! deadMessageProcessed = deadLettersProcessor.ProcessMessages messageId trySendIndividualAcknowledge
            return deadMessageProcessed
        }

    let enqueueMessage (msg: Message<'T>) =
        incomingMessagesSize <- incomingMessagesSize + msg.Data.LongLength
        incomingMessages.Enqueue(msg)

    let dequeueMessage() =
        let msg = incomingMessages.Dequeue()
        incomingMessagesSize <- incomingMessagesSize - msg.Data.LongLength
        msg
    
    
    let receiveIndividualMessagesFromBatch (rawMessage: RawMessage) (decompressedPayload: byte[]) schemaDecodeFunction =
        let batchSize = rawMessage.Metadata.NumMessages
        let acker = BatchMessageAcker(batchSize)
        let mutable skippedMessages = 0
        use stream = new MemoryStream(decompressedPayload)
        use binaryReader = new BinaryReader(stream)
        for i in 0..batchSize-1 do
            Log.Logger.LogDebug("{0} processing message num - {1} in batch", prefix, i)
            let singleMessageMetadata = Serializer.DeserializeWithLengthPrefix<SingleMessageMetadata>(stream, PrefixStyle.Fixed32BigEndian)
            let singleMessagePayload = binaryReader.ReadBytes(singleMessageMetadata.PayloadSize)

            if isSameEntry(rawMessage.MessageId) && isPriorBatchIndex(%i) then
                Log.Logger.LogDebug("{0} Ignoring message from before the startMessageId: {1} in batch", prefix, startMessageId)
                skippedMessages <- skippedMessages + 1
            else
                let messageId =
                    {
                        rawMessage.MessageId with
                            Partition = partitionIndex
                            Type = Cumulative(%i, acker)
                            TopicName = %""
                    }
                let msgKey = singleMessageMetadata.PartitionKey
                let getValue () =
                    keyValueProcessor
                    |> Option.map (fun kvp -> kvp.DecodeKeyValue(msgKey, singleMessagePayload) :?> 'T)
                    |> Option.defaultWith (fun() -> schemaDecodeFunction singleMessagePayload)
                let properties =
                    if singleMessageMetadata.Properties.Count > 0 then
                                singleMessageMetadata.Properties
                                |> Seq.map (fun prop -> (prop.Key, prop.Value))
                                |> readOnlyDict
                            else
                                EmptyProps
                let message = Message (
                                messageId,
                                singleMessagePayload,
                                %msgKey,                        
                                singleMessageMetadata.PartitionKeyB64Encoded,
                                properties,
                                getSchemaVersionBytes rawMessage.Metadata.SchemaVersion,
                                %(int64 singleMessageMetadata.SequenceId),
                                getValue
                            )
                if (rawMessage.RedeliveryCount >= deadLettersProcessor.MaxRedeliveryCount) then
                    deadLettersProcessor.AddMessage messageId message
                enqueueMessage message

        if skippedMessages > 0 then
            increaseAvailablePermits skippedMessages
    

    let hasEnoughMessagesForBatchReceive() =
        let batchReceivePolicy = consumerConfig.BatchReceivePolicy
        if (batchReceivePolicy.MaxNumMessages <= 0 && batchReceivePolicy.MaxNumBytes <= 0L) then
            false
        else
            (batchReceivePolicy.MaxNumMessages > 0 && incomingMessages.Count >= batchReceivePolicy.MaxNumMessages)
                || (batchReceivePolicy.MaxNumBytes > 0L && incomingMessagesSize >= batchReceivePolicy.MaxNumBytes)

    /// Record the event that one message has been processed by the application.
    /// Periodically, it sends a Flow command to notify the broker that it can push more messages
    let messageProcessed (msg: Message<'T>) =
        lastDequeuedMessageId <- msg.MessageId
        increaseAvailablePermits 1
        stats.UpdateNumMsgsReceived(msg.Data.Length)
        if consumerConfig.AckTimeout <> TimeSpan.Zero then
            unAckedMessageTracker.Add msg.MessageId |> ignore

    let replyWithBatch (cts: CancellationTokenSource option) (ch: AsyncReplyChannel<ResultOrException<Messages<'T>>>) =
        cts |> Option.iter (fun cts ->
            cts.Cancel()
            cts.Dispose()
        )
        let messages = Messages(consumerConfig.BatchReceivePolicy.MaxNumMessages, consumerConfig.BatchReceivePolicy.MaxNumBytes)
        let mutable shouldContinue = true
        while shouldContinue && incomingMessages.Count > 0 do
            let msgPeeked = incomingMessages.Peek()
            if (messages.CanAdd(msgPeeked)) then
                let msg = dequeueMessage()
                messageProcessed msg
                messages.Add(interceptors.BeforeConsume(this, msg))
            else
                shouldContinue <- false
        Log.Logger.LogDebug("{0} BatchFormed with size {1}", prefix, messages.Size)
        ch.Reply(Ok messages)
    
    let removeExpiredMessagesFromQueue (msgIds: RedeliverSet) =
        if incomingMessages.Count > 0 then
            let peek = incomingMessages.Peek()
            if msgIds.Contains peek.MessageId then
                // try not to remove elements that are added while we remove
                let mutable finish = false
                let mutable messagesFromQueue = 0
                while not finish && incomingMessages.Count > 0 do
                    let message = dequeueMessage()
                    messagesFromQueue <- messagesFromQueue + 1
                    if msgIds.Contains(message.MessageId) |> not then
                        msgIds.Add(message.MessageId) |> ignore
                        finish <- true
                messagesFromQueue
            else
                // first message is not expired, then no message is expired in queue.
                0
        else
            0

    let replyWithMessage (channel: AsyncReplyChannel<ResultOrException<Message<'T>>>) message =
        messageProcessed message
        let interceptMsg = interceptors.BeforeConsume(this, message)
        channel.Reply (Ok interceptMsg)

    let stopConsumer () =
        unAckedMessageTracker.Close()
        acksGroupingTracker.Close()
        clearDeadLetters()
        negativeAcksTracker.Close()
        connectionHandler.Close()      
        interceptors.Close()
        statTimer.Stop()
        cleanup(this)
        while waiters.Count > 0 do
            let waitingChannel = waiters.Dequeue()
            waitingChannel.Reply(Error (AlreadyClosedException("Consumer is already closed")))
        while batchWaiters.Count > 0 do
            let cts, batchWaitingChannel = batchWaiters.Dequeue()
            batchWaitingChannel.Reply(Error (AlreadyClosedException("Consumer is already closed")))
            cts.Cancel()
            cts.Dispose()
        Log.Logger.LogInformation("{0} stopped", prefix)

    let getDecompressPayload originalMessage =
        try
            let compressionCodec = originalMessage.Metadata.CompressionType |> CompressionCodec.create
            let uncompressedPayload = compressionCodec.Decode originalMessage.Metadata.UncompressedMessageSize originalMessage.Payload
            uncompressedPayload
        with ex ->
            Log.Logger.LogInformation(ex, "{0} Decompression exception {1}", prefix, originalMessage.MessageId)
            raise <| DecompressionException "Decompression exception"

    
    let discardCorruptedMessage (msgId: MessageId) (clientCnx: ClientCnx) error =
        stats.IncrementNumReceiveFailed()
        let command = Commands.newErrorAck consumerId msgId.LedgerId msgId.EntryId AckType.Individual error
        clientCnx.Send command
        
    let tryDiscard msgId clientCnx err =
        async {
            let! discardResult = discardCorruptedMessage msgId clientCnx err
            if discardResult then
                Log.Logger.LogInformation("{0} Message {1} was discarded due to {2}", prefix, msgId, err)
            else
                Log.Logger.LogWarning("{0} Unable to discard {1} due to {2}", prefix, msgId, err)
        }
        
    let handleMessagePayload rawMessage payload msgId hasWaitingChannel hasWaitingBatchChannel schemaDecodeFunction =
        if (acksGroupingTracker.IsDuplicate(msgId)) then
            Log.Logger.LogWarning("{0} Ignoring message as it was already being acked earlier by same consumer {1}", prefix, msgId)
            increaseAvailablePermits rawMessage.Metadata.NumMessages
        else
            if (rawMessage.Metadata.NumMessages = 1 && not rawMessage.Metadata.HasNumMessagesInBatch) then
                if isSameEntry(rawMessage.MessageId) && isPriorEntryIndex(rawMessage.MessageId.EntryId) then
                    // We need to discard entries that were prior to startMessageId
                    Log.Logger.LogInformation("{0} Ignoring message from before the startMessageId: {1}", prefix, startMessageId)
                else
                    let msgKey = rawMessage.MessageKey                    
                    let getValue () =
                        keyValueProcessor
                        |> Option.map (fun kvp -> kvp.DecodeKeyValue(msgKey, payload) :?> 'T)
                        |> Option.defaultWith (fun () -> schemaDecodeFunction payload)
                    let message = Message(
                                    msgId,
                                    payload,
                                    %msgKey,
                                    rawMessage.IsKeyBase64Encoded,
                                    rawMessage.Properties,
                                    getSchemaVersionBytes rawMessage.Metadata.SchemaVersion,
                                    rawMessage.Metadata.SequenceId,
                                    getValue
                                )
                    if (rawMessage.RedeliveryCount >= deadLettersProcessor.MaxRedeliveryCount) then
                        deadLettersProcessor.AddMessage message.MessageId message
                    if hasWaitingChannel then
                        let waitingChannel = waiters.Dequeue()
                        if (incomingMessages.Count = 0) then
                            replyWithMessage waitingChannel message
                        else
                            enqueueMessage message
                            replyWithMessage waitingChannel <| dequeueMessage()       
                    else
                        enqueueMessage message
                        if hasWaitingBatchChannel && hasEnoughMessagesForBatchReceive() then
                            let cts, ch = batchWaiters.Dequeue()
                            replyWithBatch (Some cts) ch
                            
            elif rawMessage.Metadata.NumMessages > 0 then
                // handle batch message enqueuing; uncompressed payload has all messages in batch
                try
                    receiveIndividualMessagesFromBatch rawMessage payload schemaDecodeFunction
                with ex ->
                    Log.Logger.LogError(ex, "{0} Batch reading exception {1}", prefix, msgId)
                    raise <| BatchDeserializationException "Batch reading exception"
                if hasWaitingChannel && incomingMessages.Count > 0 then
                    let waitingChannel = waiters.Dequeue()
                    replyWithMessage waitingChannel <| dequeueMessage()
            else
                Log.Logger.LogWarning("{0} Received message with nonpositive numMessages: {1}", prefix, rawMessage.Metadata.NumMessages)
        
    
    let consumerIsReconnectedToBroker() =
        Log.Logger.LogInformation("{0} subscribed to topic {1}", prefix, consumerConfig.Topic)
        avalablePermits <- 0

    let consumerOperations = {
        MessageReceived = fun (rawMessage) -> this.Mb.Post(MessageReceived rawMessage)
        ReachedEndOfTheTopic = fun () -> this.Mb.Post(ReachedEndOfTheTopic)
        ActiveConsumerChanged = fun (isActive) -> this.Mb.Post(ActiveConsumerChanged isActive)
        ConnectionClosed = fun (clientCnx) -> this.Mb.Post(ConnectionClosed clientCnx)
    }
    
    let mb = MailboxProcessor<ConsumerMessage<'T>>.Start(fun inbox ->

        let rec loop () =
            async {
                let! msg = inbox.Receive()
                match msg with
                | ConsumerMessage.ConnectionOpened ->

                    match connectionHandler.ConnectionState with
                    | Ready clientCnx ->
                        Log.Logger.LogInformation("{0} starting subscribe to topic {1}", prefix, consumerConfig.Topic)
                        clientCnx.AddConsumer(consumerId, consumerOperations)
                        let requestId = Generators.getNextRequestId()
                        startMessageId <- clearReceiverQueue()
                        clearDeadLetters()
                        let msgIdData =
                            if isDurable then
                                null
                            else
                                match startMessageId with
                                | None ->
                                    Log.Logger.LogWarning("{0} Start messageId is missing")
                                    null
                                | Some msgId ->
                                    let data = MessageIdData(ledgerId = uint64 %msgId.LedgerId, entryId = uint64 %msgId.EntryId)
                                    match msgId.Type with
                                    | Individual ->
                                        ()
                                    | Cumulative (index, _) ->
                                        data.BatchIndex <- %index
                                    data
                        // startMessageRollbackDurationInSec should be consider only once when consumer connects to first time
                        let startMessageRollbackDuration =
                            if startMessageRollbackDuration > TimeSpan.Zero && startMessageId = initialStartMessageId then
                                startMessageRollbackDuration
                            else
                                TimeSpan.Zero
                        let payload =
                            Commands.newSubscribe
                                consumerConfig.Topic.CompleteTopicName consumerConfig.SubscriptionName
                                consumerId requestId consumerConfig.ConsumerName consumerConfig.SubscriptionType
                                consumerConfig.SubscriptionInitialPosition consumerConfig.ReadCompacted msgIdData isDurable
                                startMessageRollbackDuration createTopicIfDoesNotExist consumerConfig.KeySharedPolicy schema.SchemaInfo
                        try
                            let! response = clientCnx.SendAndWaitForReply requestId payload |> Async.AwaitTask
                            response |> PulsarResponseType.GetEmpty
                            consumerIsReconnectedToBroker()
                            connectionHandler.ResetBackoff()
                            let initialFlowCount = consumerConfig.ReceiverQueueSize
                            subscribeTsc.TrySetResult() |> ignore
                            if initialFlowCount <> 0 then
                                let flowCommand = Commands.newFlow consumerId initialFlowCount
                                let! success = clientCnx.Send flowCommand
                                if success then
                                    Log.Logger.LogDebug("{0} initial flow sent {1}", prefix, initialFlowCount)
                                else
                                    raise (ConnectionFailedOnSend "FlowCommand")
                        with Flatten ex ->
                            clientCnx.RemoveConsumer consumerId
                            Log.Logger.LogError(ex, "{0} failed to subscribe to topic", prefix)
                            if (PulsarClientException.isRetriableError ex && DateTime.Now < subscribeTimeout) then
                                connectionHandler.ReconnectLater ex
                            else
                                if not subscribeTsc.Task.IsCompleted then
                                    // unable to create new consumer, fail operation
                                    connectionHandler.Failed()
                                    subscribeTsc.SetException(ex)
                                    stopConsumer()
                                else
                                    connectionHandler.ReconnectLater ex
                    | _ ->
                        Log.Logger.LogWarning("{0} connection opened but connection is not ready", prefix)
                    return! loop ()

                | ConsumerMessage.ConnectionClosed clientCnx ->

                    Log.Logger.LogDebug("{0} ConnectionClosed", prefix)
                    connectionHandler.ConnectionClosed clientCnx
                    clientCnx.RemoveConsumer(consumerId)
                    return! loop ()

                | ConsumerMessage.ConnectionFailed ex ->

                    Log.Logger.LogDebug("{0} ConnectionFailed", prefix)
                    if (DateTime.Now > subscribeTimeout && subscribeTsc.TrySetException(ex)) then
                        Log.Logger.LogInformation("{0} creation failed", prefix)
                        connectionHandler.Failed()
                        stopConsumer()
                    else
                        return! loop ()

                | ConsumerMessage.MessageReceived (rawMessage, clientCnx) ->

                    let hasWaitingChannel = waiters.Count > 0
                    let hasWaitingBatchChannel = batchWaiters.Count > 0
                    let msgId = getNewIndividualMsgIdWithPartition rawMessage.MessageId
                    Log.Logger.LogDebug("{0} MessageReceived {1} queueLength={2}, hasWaitingChannel={3},  hasWaitingBatchChannel={4}",
                        prefix, msgId, incomingMessages.Count, hasWaitingChannel, hasWaitingBatchChannel)

                    if rawMessage.CheckSumValid then
                        if rawMessage.Payload.Length <= clientCnx.MaxMessageSize then
                            try
                                let payload = getDecompressPayload rawMessage
                                // mutable workaround for unsuppported let! inside let
                                let mutable schemaDecodeFunction = Unchecked.defaultof<(byte[]->'T)>
                                if schemaProvider.IsNone || rawMessage.Metadata.SchemaVersion.IsNone then
                                    schemaDecodeFunction <- schema.Decode
                                else
                                    let schemaVersion = rawMessage.Metadata.SchemaVersion.Value
                                    let! specificSchemaOption = schemaProvider.Value.GetSchemaByVersion(schema, schemaVersion) |> Async.AwaitTask
                                    schemaDecodeFunction <-
                                        match specificSchemaOption with
                                        | Some specificSchema -> specificSchema.Decode
                                        | None -> schema.Decode
                                handleMessagePayload rawMessage payload msgId hasWaitingChannel hasWaitingBatchChannel schemaDecodeFunction
                            with
                                | DecompressionException _ ->
                                    do! tryDiscard msgId clientCnx CommandAck.ValidationError.DecompressionError
                                | BatchDeserializationException _ ->
                                    do! tryDiscard msgId clientCnx CommandAck.ValidationError.BatchDeSerializeError
                        else
                            do! tryDiscard msgId clientCnx CommandAck.ValidationError.UncompressedSizeCorruption
                    else
                        do! tryDiscard msgId clientCnx CommandAck.ValidationError.ChecksumMismatch
                    return! loop ()

                | ConsumerMessage.Receive ch ->

                    Log.Logger.LogDebug("{0} Receive", prefix)
                    if incomingMessages.Count > 0 then
                        replyWithMessage ch <| dequeueMessage()
                    else
                        waiters.Enqueue(ch)
                        Log.Logger.LogDebug("{0} Receive waiting", prefix)
                    return! loop ()
                    
                | ConsumerMessage.BatchReceive ch ->

                    Log.Logger.LogDebug("{0} BatchReceive", prefix)
                    if batchWaiters.Count = 0 && hasEnoughMessagesForBatchReceive() then
                        replyWithBatch None ch
                    else
                        let ct = new CancellationTokenSource()
                        batchWaiters.Enqueue(ct, ch)
                        asyncCancellableDelay
                            (int consumerConfig.BatchReceivePolicy.Timeout.TotalMilliseconds)
                            (fun () -> this.Mb.Post(SendBatchByTimeout))
                            ct.Token
                        Log.Logger.LogDebug("{0} BatchReceive waiting", prefix)
                    return! loop ()
                    
                | ConsumerMessage.SendBatchByTimeout ->
                    
                    Log.Logger.LogDebug("{0} SendBatchByTimeout", prefix)
                    if batchWaiters.Count > 0 then
                        let cts, ch = batchWaiters.Dequeue()
                        replyWithBatch (Some cts) ch
                    return! loop ()

                | ConsumerMessage.Acknowledge (messageId, ackType) ->

                    Log.Logger.LogDebug("{0} Acknowledge {1} {2}", prefix, messageId, ackType)
                    do! trySendAcknowledge ackType messageId
                    return! loop ()
                    
                | ConsumerMessage.NegativeAcknowledge messageId ->

                    Log.Logger.LogDebug("{0} NegativeAcknowledge {1}", prefix, messageId)                    
                    negativeAcksTracker.Add(messageId) |> ignore
                    // Ensure the message is not redelivered for ack-timeout, since we did receive an "ack"
                    unAckedMessageTracker.Remove(messageId) |> ignore
                    return! loop ()

                | ConsumerMessage.RedeliverUnacknowledged (messageIds, channel) ->

                    Log.Logger.LogDebug("{0} RedeliverUnacknowledged", prefix)
                    match consumerConfig.SubscriptionType with
                    | SubscriptionType.Shared | SubscriptionType.KeyShared ->
                        match connectionHandler.ConnectionState with
                        | Ready clientCnx ->
                            let messagesFromQueue = removeExpiredMessagesFromQueue(messageIds);
                            let chunks = messageIds |> Seq.chunkBySize MAX_REDELIVER_UNACKNOWLEDGED
                            for chunk in chunks do
                                let nonDeadBatch = ResizeArray<MessageId>()
                                for messageId in chunk do
                                    let! isDead = processDeadLetters messageId |> Async.AwaitTask
                                    if not isDead then
                                        nonDeadBatch.Add messageId
                                if nonDeadBatch.Count > 0 then
                                    let command = Commands.newRedeliverUnacknowledgedMessages consumerId (
                                                        Some(nonDeadBatch |> Seq.map (fun msgId -> MessageIdData(Partition = msgId.Partition, ledgerId = uint64 %msgId.LedgerId, entryId = uint64 %msgId.EntryId)))
                                                    )
                                    let! success = clientCnx.Send command
                                    if success then
                                        Log.Logger.LogDebug("{0} RedeliverAcknowledged complete", prefix)
                                    else
                                        Log.Logger.LogWarning("{0} RedeliverAcknowledged was not complete", prefix)
                                else
                                    Log.Logger.LogDebug("{0} All messages were dead", prefix)
                            if messagesFromQueue > 0 then
                                increaseAvailablePermits messagesFromQueue
                        | _ ->
                            Log.Logger.LogWarning("{0} not connected, skipping send", prefix)
                        channel.Reply()
                    | _ ->
                        this.Mb.Post(RedeliverAllUnacknowledged channel)
                        Log.Logger.LogInformation("{0} We cannot redeliver single messages if subscription type is not Shared", prefix)
                    return! loop ()

                | ConsumerMessage.RedeliverAllUnacknowledged channel ->

                    Log.Logger.LogDebug("{0} RedeliverAllUnacknowledged", prefix)
                    match connectionHandler.ConnectionState with
                    | Ready clientCnx ->
                        let command = Commands.newRedeliverUnacknowledgedMessages consumerId None
                        let! success = clientCnx.Send command
                        if success then
                            let currentSize = incomingMessages.Count
                            if currentSize > 0 then
                                incomingMessages.Clear()
                                increaseAvailablePermits currentSize
                                unAckedMessageTracker.Clear()
                            Log.Logger.LogDebug("{0} RedeliverAllUnacknowledged complete", prefix)
                        else
                            Log.Logger.LogWarning("{0} RedeliverAllUnacknowledged was not complete", prefix)
                    | _ ->
                        Log.Logger.LogWarning("{0} not connected, skipping send", prefix)
                    channel.Reply()
                    return! loop ()

                | ConsumerMessage.SeekAsync (seekData, channel) ->

                    Log.Logger.LogDebug("{0} SeekAsync", prefix)
                    match connectionHandler.ConnectionState with
                    | Ready clientCnx ->
                        let requestId = Generators.getNextRequestId()
                        Log.Logger.LogInformation("{0} Seek subscription to {1}", prefix, seekData)
                        try
                            let (payload, lastMessage) =
                                match seekData with
                                | Timestamp timestamp -> Commands.newSeekByTimestamp consumerId requestId timestamp, MessageId.Earliest
                                | MessageId messageId -> Commands.newSeekByMsgId consumerId requestId messageId, messageId
                            let! response = clientCnx.SendAndWaitForReply requestId payload |> Async.AwaitTask
                            response |> PulsarResponseType.GetEmpty
                            
                            duringSeek <- Some lastMessage
                            lastDequeuedMessageId <- MessageId.Earliest
                            
                            acksGroupingTracker.FlushAndClean()
                            incomingMessages.Clear()
                            Log.Logger.LogInformation("{0} Successfully reset subscription to {1}", prefix, seekData)
                            channel.Reply <| Ok()
                        with Flatten ex ->
                            Log.Logger.LogError(ex, "{0} Failed to reset subscription to {1}", prefix, seekData)
                            channel.Reply <| Error ex
                    | _ ->
                        channel.Reply <| Error(NotConnectedException "Not connected to broker")
                        Log.Logger.LogError("{0} not connected, skipping SeekAsync {1}", prefix, seekData)
                    return! loop ()

                | ConsumerMessage.SendFlowPermits numMessages ->

                    Log.Logger.LogDebug("{0} SendFlowPermits {1}", prefix, numMessages)
                    match connectionHandler.ConnectionState with
                    | Ready clientCnx ->
                        let flowCommand = Commands.newFlow consumerId numMessages
                        let! success = clientCnx.Send flowCommand
                        if not success then
                            Log.Logger.LogWarning("{0} failed SendFlowPermits {1}", prefix, numMessages)
                    | _ ->
                        Log.Logger.LogWarning("{0} not connected, skipping SendFlowPermits {1}", prefix, numMessages)
                    return! loop ()

                | ConsumerMessage.ReachedEndOfTheTopic ->

                    Log.Logger.LogWarning("{0} ReachedEndOfTheTopic", prefix)
                    hasReachedEndOfTopic <- true

                | ConsumerMessage.HasMessageAvailable channel ->

                    Log.Logger.LogDebug("{0} HasMessageAvailable", prefix)
                    
                    // avoid null referenece
                    let startMessageId = startMessageId |> Option.defaultValue lastDequeuedMessageId
                    
                    // we haven't read yet. use startMessageId for comparison
                    if lastDequeuedMessageId = MessageId.Earliest then
                        // if we are starting from latest, we should seek to the actual last message first.
                        // allow the last one to be read when read head inclusively.
                        if startMessageId = MessageId.Latest then                            
                            let! messageId = getLastMessageIdAsync() |> Async.AwaitTask
                            task {
                                let! result = this.Mb.PostAndAsyncReply(fun channel -> SeekAsync ((MessageId messageId), channel))
                                return
                                    match result with
                                    | Ok () -> consumerConfig.ResetIncludeHead
                                    | Error ex -> reraize ex
                            } |> channel.Reply
                        elif hasMoreMessages this.LastMessageIdInBroker startMessageId consumerConfig.ResetIncludeHead then
                            channel.Reply(Task.FromResult(true))
                        else
                            task {
                                let! messageId = getLastMessageIdAsync()
                                this.LastMessageIdInBroker <- messageId // Concurrent update - handle wisely
                                return hasMoreMessages this.LastMessageIdInBroker startMessageId consumerConfig.ResetIncludeHead
                            } |> channel.Reply

                    else
                        // read before, use lastDequeueMessage for comparison
                        if hasMoreMessages this.LastMessageIdInBroker lastDequeuedMessageId false then
                            channel.Reply(Task.FromResult(true))
                        else
                            task {
                                let! messageId = getLastMessageIdAsync()
                                this.LastMessageIdInBroker <- messageId // Concurrent update - handle wisely
                                return hasMoreMessages this.LastMessageIdInBroker lastDequeuedMessageId false
                            } |> channel.Reply

                    return! loop ()
                                    
                | ConsumerMessage.ActiveConsumerChanged isActive ->
                    
                    Log.Logger.LogInformation("{0} ActiveConsumerChanged isActive={1}", prefix, isActive)
                    return! loop ()
                        
                | ConsumerMessage.StatTick ->
                    
                    stats.TickTime(incomingMessages.Count)
                    return! loop()
                    
                | ConsumerMessage.GetStats channel ->
                    
                    channel.Reply <| stats.GetStats()
                    return! loop ()
                    
                | ConsumerMessage.Close channel ->

                    match connectionHandler.ConnectionState with
                    | Ready clientCnx ->
                        connectionHandler.Closing()
                        Log.Logger.LogInformation("{0} starting close", prefix)
                        let requestId = Generators.getNextRequestId()
                        let payload = Commands.newCloseConsumer consumerId requestId
                        try
                            let! response = clientCnx.SendAndWaitForReply requestId payload |> Async.AwaitTask
                            response |> PulsarResponseType.GetEmpty
                            clientCnx.RemoveConsumer(consumerId)
                            connectionHandler.Closed()
                            stopConsumer()
                            channel.Reply <| Ok()
                        with Flatten ex ->
                            Log.Logger.LogError(ex, "{0} failed to close", prefix)
                            channel.Reply <| Error ex
                    | _ ->
                        Log.Logger.LogInformation("{0} closing but current state {1}", prefix, connectionHandler.ConnectionState)
                        connectionHandler.Closed()
                        stopConsumer()
                        channel.Reply <| Ok()

                | ConsumerMessage.Unsubscribe channel ->

                    match connectionHandler.ConnectionState with
                    | Ready clientCnx ->
                        connectionHandler.Closing()
                        unAckedMessageTracker.Close()
                        clearDeadLetters()
                        Log.Logger.LogInformation("{0} starting unsubscribe ", prefix)
                        let requestId = Generators.getNextRequestId()
                        let payload = Commands.newUnsubscribeConsumer consumerId requestId                       
                        try
                            let! response = clientCnx.SendAndWaitForReply requestId payload |> Async.AwaitTask
                            response |> PulsarResponseType.GetEmpty
                            clientCnx.RemoveConsumer(consumerId)
                            connectionHandler.Closed()
                            stopConsumer()
                            channel.Reply <| Ok()
                        with Flatten ex ->
                            Log.Logger.LogError(ex, "{0} failed to unsubscribe", prefix)
                            channel.Reply <| Error ex
                    | _ ->
                        Log.Logger.LogError("{0} can't unsubscribe since not connected", prefix)
                        channel.Reply <| Error(NotConnectedException "Not connected to broker")
                        return! loop ()
            }
        loop ()
    )
    do mb.Error.Add(fun ex -> Log.Logger.LogCritical(ex, "{0} mailbox failure", prefix))
    do startStatTimer()

    member private this.Mb with get(): MailboxProcessor<ConsumerMessage<'T>> = mb

    member this.ConsumerId with get() = consumerId

    member this.HasMessageAvailableAsync() =
        task {
            connectionHandler.CheckIfActive() |> throwIfNotNull
            let! result = mb.PostAndAsyncReply(fun channel -> HasMessageAvailable channel)
            return! result
        }

    member this.LastMessageIdInBroker
        with get() = Volatile.Read(&lastMessageIdInBroker)
        and private set(value) = Volatile.Write(&lastMessageIdInBroker, value)
    
    override this.Equals consumer =
        consumerId = (consumer :?> IConsumer<'T>).ConsumerId

    override this.GetHashCode () = int consumerId
    
    
    member this.InitInternal() =
        task {
            do connectionHandler.GrabCnx()
            return! subscribeTsc.Task
        }

    static member Init(consumerConfig: ConsumerConfiguration<'T>, clientConfig: PulsarClientConfiguration, connectionPool: ConnectionPool,
                       partitionIndex: int, startMessageId: MessageId option, lookup: BinaryLookupService,
                       createTopicIfDoesNotExist: bool, schema: ISchema<'T>, schemaProvider: MultiVersionSchemaInfoProvider option,
                       interceptors: ConsumerInterceptors<'T>, cleanup: ConsumerImpl<'T> -> unit) =
        task {
            let consumer = ConsumerImpl(consumerConfig, clientConfig, connectionPool, partitionIndex,
                                        startMessageId, lookup, TimeSpan.Zero, createTopicIfDoesNotExist,
                                        schema, schemaProvider, interceptors, cleanup)
            do! consumer.InitInternal()
            return consumer
        }

    member this.ReceiveFsharpAsync() =
        async {
            let exn = connectionHandler.CheckIfActive()
            if not (isNull exn) then
                raise exn

            let! msgResult = mb.PostAndAsyncReply(Receive)
            match msgResult with
            | Ok _ ->
                return msgResult
            | Error _ ->
                stats.IncrementNumReceiveFailed()
                return msgResult
        }
    
    interface IConsumer<'T> with
        
        member this.ReceiveAsync() =
            task {
                let exn = connectionHandler.CheckIfActive()
                if not (isNull exn) then
                    raise exn

                match! mb.PostAndAsyncReply(Receive) with
                | Ok msg ->
                    return msg
                | Error exn ->
                    stats.IncrementNumReceiveFailed()
                    return reraize exn
            }

        member this.BatchReceiveAsync() =
            task {
                let exn = connectionHandler.CheckIfActive()
                if not (isNull exn) then
                    raise exn

                match! mb.PostAndAsyncReply(BatchReceive) with
                | Ok msg ->
                    return msg
                | Error exn ->
                    stats.IncrementNumBatchReceiveFailed()
                    return reraize exn
            }

        member this.AcknowledgeAsync (msgId: MessageId) =
            task {
                let exn = connectionHandler.CheckIfActive()
                if not (isNull exn) then
                    stats.IncrementNumAcksFailed()
                    interceptors.OnAcknowledge(this, msgId, exn)
                    raise exn
                
                mb.Post(Acknowledge(msgId, AckType.Individual))
            }
            
        member this.AcknowledgeAsync (msgs: Messages<'T>) =
            task {
                let exn = connectionHandler.CheckIfActive()
                if not (isNull exn) then
                    for msg in msgs do
                        stats.IncrementNumAcksFailed()
                        interceptors.OnAcknowledge(this, msg.MessageId, exn)
                    raise exn

                for msg in msgs do
                    mb.Post(Acknowledge(msg.MessageId, AckType.Individual))
            }

        member this.AcknowledgeCumulativeAsync (msgId: MessageId) =
            task {
                let exn = connectionHandler.CheckIfActive()
                if not (isNull exn) then
                    stats.IncrementNumAcksFailed()
                    interceptors.OnAcknowledgeCumulative(this, msgId, exn)
                    raise exn

                mb.Post(Acknowledge(msgId, AckType.Cumulative))
            }

        member this.RedeliverUnacknowledgedMessagesAsync () =
            task {
                connectionHandler.CheckIfActive() |> throwIfNotNull
                do! mb.PostAndAsyncReply(RedeliverAllUnacknowledged)
            }

        member this.SeekAsync (messageId: MessageId) =
            task {
                connectionHandler.CheckIfActive() |> throwIfNotNull
                return! wrapPostAndReply <| mb.PostAndAsyncReply(fun channel -> SeekAsync (MessageId messageId, channel))
            }

        member this.SeekAsync (timestamp: uint64) =
            task {
                connectionHandler.CheckIfActive() |> throwIfNotNull
                return! wrapPostAndReply <| mb.PostAndAsyncReply(fun channel -> SeekAsync (Timestamp timestamp, channel))
            }
            
        member this.GetLastMessageIdAsync () =
            getLastMessageIdAsync()

        member this.UnsubscribeAsync() =
            task {
                connectionHandler.CheckIfActive() |> throwIfNotNull
                return! wrapPostAndReply <| mb.PostAndAsyncReply(ConsumerMessage.Unsubscribe)
            }

        member this.HasReachedEndOfTopic with get() = hasReachedEndOfTopic

        member this.NegativeAcknowledge msgId =
            task {
                connectionHandler.CheckIfActive() |> throwIfNotNull
                mb.Post(NegativeAcknowledge(msgId))
            }

        member this.NegativeAcknowledge (msgs: Messages<'T>)  =
            task {
                connectionHandler.CheckIfActive() |> throwIfNotNull 
                for msg in msgs do
                    mb.Post(NegativeAcknowledge(msg.MessageId))
            }
        
        member this.ConsumerId = consumerId

        member this.Topic with get() = %consumerConfig.Topic.CompleteTopicName

        member this.Name = consumerConfig.ConsumerName

        member this.GetStatsAsync() =
            mb.PostAndAsyncReply(ConsumerMessage.GetStats) |> Async.StartAsTask
        
    interface IAsyncDisposable with
        
        member this.DisposeAsync() =     
            match connectionHandler.ConnectionState with
            | Closing | Closed ->
                ValueTask()
            | _ ->
                task {
                    let! result = mb.PostAndAsyncReply(ConsumerMessage.Close)
                    match result with
                    | Ok () -> ()
                    | Error ex -> reraize ex 
                } |> ValueTask

