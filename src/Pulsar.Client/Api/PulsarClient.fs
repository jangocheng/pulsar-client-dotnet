﻿namespace Pulsar.Client.Api

open System
open FSharp.Control.Tasks.V2.ContextInsensitive
open Pulsar.Client.Internal
open Microsoft.Extensions.Logging
open System.Collections.Generic
open System.Threading.Tasks
open Pulsar.Client.Common
open System.Threading
open Pulsar.Client.Schema

type internal PulsarClientState =
    | Active
    | Closing
    | Closed

type internal PulsarClientMessage =
    | RemoveProducer of IAsyncDisposable // IProducer
    | RemoveConsumer of IAsyncDisposable // IConsumer
    | AddProducer of IAsyncDisposable // IProducer
    | AddConsumer of IAsyncDisposable // IConsumer
    | GetSchemaProvider of AsyncReplyChannel<MultiVersionSchemaInfoProvider> * CompleteTopicName
    | Close of AsyncReplyChannel<Task>
    | Stop

type PulsarClient(config: PulsarClientConfiguration) as this =

    let connectionPool = ConnectionPool(config)
    let lookupService = BinaryLookupService(config, connectionPool)
    let producers = HashSet<IAsyncDisposable>()
    let consumers = HashSet<IAsyncDisposable>()
    let schemaProviders = Dictionary<CompleteTopicName, MultiVersionSchemaInfoProvider>() 
    let mutable clientState = Active
    let autoProduceStubType =  typeof<AutoProduceBytesSchemaStub>
    let autoConsumeStubType =  typeof<AutoConsumeSchemaStub>

    let tryStopMailbox() =
        match this.ClientState with
        | Closing ->
            if consumers.Count = 0 && producers.Count = 0 then
                this.Mb.Post(Stop)
        | _ ->
            ()

    let checkIfActive() =
        match this.ClientState with
        | Active ->  ()
        | _ ->  raise <| AlreadyClosedException("Client already closed. State: " + this.ClientState.ToString())

    let mb = MailboxProcessor<PulsarClientMessage>.Start(fun inbox ->

        let rec loop () =
            async {
                let! msg = inbox.Receive()
                match msg with
                | RemoveProducer producer ->
                    producers.Remove(producer) |> ignore
                    tryStopMailbox()
                    return! loop ()
                | RemoveConsumer consumer ->
                    consumers.Remove(consumer) |> ignore
                    tryStopMailbox ()
                    return! loop ()
                | AddProducer producer ->
                    producers.Add producer |> ignore
                    return! loop ()
                | AddConsumer consumer ->
                    consumers.Add consumer |> ignore
                    return! loop ()
                | GetSchemaProvider (channel, topicName) ->
                    match schemaProviders.TryGetValue(topicName) with
                    | true, provider -> channel.Reply(provider)
                    | false, _ ->
                        let provider = 
                           MultiVersionSchemaInfoProvider(fun schemaVersion ->
                               lookupService.GetSchema(topicName, schemaVersion))
                        schemaProviders.Add(topicName, provider)
                        channel.Reply(provider)
                    return! loop()                    
                | Close channel ->
                    match this.ClientState with
                    | Active ->
                        Log.Logger.LogInformation("Client closing. URL: {0}", config.ServiceAddresses)
                        this.ClientState <- Closing
                        let producersTasks = producers |> Seq.map (fun producer -> task { return! producer.DisposeAsync() } )
                        let consumerTasks = consumers |> Seq.map (fun consumer -> task { return! consumer.DisposeAsync() })
                        task {
                            try
                                let! _ = Task.WhenAll (seq { yield! producersTasks; yield! consumerTasks })
                                schemaProviders |> Seq.iter (fun (KeyValue (_, provider)) -> provider.Close())
                                tryStopMailbox()
                            with ex ->
                                Log.Logger.LogError(ex, "Couldn't stop client")
                                this.ClientState <- Active
                        } |> channel.Reply
                        return! loop ()
                    | _ ->
                        channel.Reply(Task.FromException(AlreadyClosedException("Client already closed. URL: " + config.ServiceAddresses.ToString())))
                        return! loop ()
                | Stop ->
                    this.ClientState <- Closed
                    connectionPool.Close()
                    Log.Logger.LogInformation("Pulsar client stopped")
            }
        loop ()
    )

    do mb.Error.Add(fun ex -> Log.Logger.LogCritical(ex, "PulsarClient mailbox failure"))

    static member Logger
        with get () = Log.Logger
        and set (value) = Log.Logger <- value

    member internal this.SubscribeAsync(consumerConfig, schema, interceptors) =
        task {
            checkIfActive()
            return! this.SingleTopicSubscribeAsync(consumerConfig, schema, interceptors)
        }

    member this.CloseAsync() =
        task {
            checkIfActive()
            let! t = mb.PostAndAsyncReply(Close)
            return! t
        }
        
    member private this.GetPartitionedTopicMetadata(topicName, backoff: Backoff, remainingTimeMs) =
        async {
            try
                return! lookupService.GetPartitionedTopicMetadata topicName |> Async.AwaitTask
            with Flatten ex ->
                let delay = Math.Min(backoff.Next(), remainingTimeMs)
                // skip retry scheduler when set lookup throttle in client or server side which will lead to `TooManyRequestsException`                
                let isLookupThrottling = PulsarClientException.isRetriableError ex |> not
                if delay <= 0 || isLookupThrottling then
                    reraize ex
                Log.Logger.LogWarning(ex, "Could not get connection while getPartitionedTopicMetadata -- Will try again in {0} ms", delay)
                do! Async.Sleep delay
                return! this.GetPartitionedTopicMetadata(topicName, backoff, remainingTimeMs - delay)
        }
        
    member private this.GetPartitionedTopicMetadata(topicName) =
        task {
            checkIfActive()
            let backoff = Backoff { BackoffConfig.Default with
                                        Initial = TimeSpan.FromMilliseconds(100.0)
                                        MandatoryStop = (config.OperationTimeout + config.OperationTimeout)
                                        Max = TimeSpan.FromMinutes(1.0) }
            return! this.GetPartitionedTopicMetadata(topicName, backoff, int config.OperationTimeout.TotalMilliseconds)
        }
    
    member private this.PreProcessSchemaBeforeSubscribe(schema: ISchema<'T>, topicName) =
        task {
            if schema.SupportSchemaVersioning then
                let! provider = mb.PostAndAsyncReply(fun (channel) -> GetSchemaProvider(channel, topicName))
                return Some provider
            else
                return None
        }

    member private this.SingleTopicSubscribeAsync (consumerConfig: ConsumerConfiguration<'T>, schema: ISchema<'T>, interceptors: ConsumerInterceptors<'T>) =
        task {
            checkIfActive()
            Log.Logger.LogDebug("SingleTopicSubscribeAsync started")
            let! schemaProvider = this.PreProcessSchemaBeforeSubscribe(schema, consumerConfig.Topic.CompleteTopicName)
            let! metadata = this.GetPartitionedTopicMetadata consumerConfig.Topic.CompleteTopicName
            let mutable activeSchema = schema
            if schema.GetType() = autoConsumeStubType then
                match! lookupService.GetSchema(consumerConfig.Topic.CompleteTopicName) with
                | Some schemaData ->
                    let autoSchema = Schema.GetAutoConsumeSchema schemaData |> box
                    activeSchema <- autoSchema |> unbox
                | None ->
                    ()          
            let removeConsumer = fun consumer -> mb.Post(RemoveConsumer consumer)
            if (metadata.Partitions > 0)
            then
                let! consumer = MultiTopicsConsumerImpl.Init(consumerConfig, config, connectionPool, metadata.Partitions,
                                                             lookupService, activeSchema, schemaProvider, interceptors, removeConsumer)
                mb.Post(AddConsumer consumer)
                return consumer :> IConsumer<'T>
            else
                let! consumer = ConsumerImpl.Init(consumerConfig, config, connectionPool, -1, None, lookupService, true,
                                                  activeSchema, schemaProvider, interceptors, removeConsumer)
                mb.Post(AddConsumer consumer)
                return consumer :> IConsumer<'T>
        }

    member internal this.CreateProducerAsync<'T> (producerConfig: ProducerConfiguration, schema: ISchema<'T>, interceptors: ProducerInterceptors<'T>) =
        task {
            checkIfActive()
            Log.Logger.LogDebug("CreateProducerAsync started")
            let! metadata = this.GetPartitionedTopicMetadata producerConfig.Topic.CompleteTopicName
            let mutable activeSchema = schema
            if schema.GetType() = autoProduceStubType then
                match! lookupService.GetSchema(producerConfig.Topic.CompleteTopicName) with
                | Some schemaInfo ->
                    let validate = Schema.GetValidateFunction schemaInfo
                    let autoProduceSchema = AutoProduceBytesSchema(schemaInfo.SchemaInfo.Name, schemaInfo.SchemaInfo.Type, schemaInfo.SchemaInfo.Schema, validate) |> box
                    activeSchema <- autoProduceSchema |> unbox
                | None ->
                    ()                    
            let removeProducer = fun producer -> mb.Post(RemoveProducer producer)
            if (metadata.Partitions > 0) then
                let! producer = PartitionedProducerImpl.Init(producerConfig, config, connectionPool, metadata.Partitions,
                                                             lookupService, activeSchema, interceptors, removeProducer)
                mb.Post(AddProducer producer)
                return producer :> IProducer<'T>
            else
                let! producer = ProducerImpl.Init(producerConfig, config, connectionPool, -1, lookupService,
                                                  activeSchema, interceptors, removeProducer)
                mb.Post(AddProducer producer)
                return producer :> IProducer<'T>
        }

    member internal this.CreateReaderAsync<'T> (readerConfig: ReaderConfiguration, schema: ISchema<'T>) =
        task {
            checkIfActive()
            Log.Logger.LogDebug("CreateReaderAsync started")
            let! metadata = this.GetPartitionedTopicMetadata readerConfig.Topic.CompleteTopicName
            let! schemaProvider = this.PreProcessSchemaBeforeSubscribe(schema, readerConfig.Topic.CompleteTopicName)
            if (metadata.Partitions > 0)
            then
                return failwith "Topic reader cannot be created on a partitioned topic"
            else
                let! reader = ReaderImpl.Init(readerConfig, config, connectionPool, schema, schemaProvider, lookupService)
                mb.Post(AddConsumer reader)
                return reader
        }

    member private this.Mb with get(): MailboxProcessor<PulsarClientMessage> = mb

    member private this.ClientState
        with get() = Volatile.Read(&clientState)
        and set(value) = Volatile.Write(&clientState, value)
        
    member this.NewProducer() =
        ProducerBuilder(this.CreateProducerAsync, Schema.BYTES())
        
    member this.NewProducer(schema) =
        ProducerBuilder(this.CreateProducerAsync, schema)
        
    member this.NewConsumer() =
        ConsumerBuilder(this.SubscribeAsync, this.CreateProducerAsync, Schema.BYTES())
    member this.NewConsumer(schema) =
        ConsumerBuilder(this.SubscribeAsync, this.CreateProducerAsync, schema)    
    member this.NewReader() =
        ReaderBuilder(this.CreateReaderAsync, Schema.BYTES())
    member this.NewReader(schema) =
        ReaderBuilder(this.CreateReaderAsync, schema)
