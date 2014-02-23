﻿// ----------------------------------------------------------------------------
// (c) Seemant Rajvanshi, 2013
//
// This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
// copy of the license can be found in the License.txt file at the root of this distribution. 
// By using this source code in any fashion, you are agreeing to be bound 
// by the terms of the Apache License, Version 2.0.
//
// You must not remove this notice, or any other, from this software.
// ----------------------------------------------------------------------------
// ----------------------------------------------------------------------------
namespace FlexSearch.Core

// ----------------------------------------------------------------------------
open FlexSearch.Api
open FlexSearch.Api.Exception
open FlexSearch.Core
open FlexSearch.Utility
open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Data
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Threading.Tasks.Dataflow
open java.io
open java.util
open org.apache.lucene.analysis
open org.apache.lucene.analysis.core
open org.apache.lucene.analysis.miscellaneous
open org.apache.lucene.analysis.util
open org.apache.lucene.codecs
open org.apache.lucene.codecs.lucene42
open org.apache.lucene.document
open org.apache.lucene.facet.search
open org.apache.lucene.index
open org.apache.lucene.search
open org.apache.lucene.store

// ----------------------------------------------------------------------------
// Contains document building and indexing related operations
// ----------------------------------------------------------------------------
[<AutoOpen>]
[<RequireQualifiedAccess>]
module Document = 
    /// <summary>
    ///  Method to map a string based id to a lucene shard 
    /// </summary>
    /// <param name="id">Id of the document</param>
    /// <param name="shardCount">Total available shards</param>
    let mapToShard (id : string) shardCount = 
        let mutable total = 0
        for i in id do
            total <- total + System.Convert.ToInt32(i)
        total % shardCount
    
    /// Generates a lucene daocument from a flex document    
    let Generate (document : FlexSearch.Api.Document) flexIndexSetting = 
        let luceneDocument = new Document()
        luceneDocument.add (new StringField(Constants.IdField, document.Id, Field.Store.YES))
        luceneDocument.add (new StringField(Constants.TypeField, document.Index, Field.Store.YES))
        luceneDocument.add (new IntField(Constants.VersionField, document.Version, Field.Store.YES))
        luceneDocument.add (new LongField(Constants.LastModifiedField, GetCurrentTimeAsLong(), Field.Store.YES))
        for field in flexIndexSetting.Fields do
            match document.Fields.TryGetValue(field.FieldName) with
            | (true, value) -> luceneDocument.add (FlexField.CreateLuceneField field value)
            | _ -> luceneDocument.add (FlexField.CreateDefaultLuceneField field)
        luceneDocument
    
    // Add a flex document to an index    
    let Add (document : FlexSearch.Api.Document) flexIndex optimistic (versionCache : IVersioningCacheStore) = 
        if (System.String.IsNullOrWhiteSpace(document.Id) = true) then failwith "Missing Id"
        let targetIndex = mapToShard document.Id flexIndex.Shards.Length
        let targetDocument = Generate document flexIndex.IndexSetting
        flexIndex.Shards.[targetIndex].TrackingIndexWriter.addDocument(targetDocument)
    
    // Update a flex document in an index    
    let Update (document : FlexSearch.Api.Document) flexIndex = 
        if (System.String.IsNullOrWhiteSpace(document.Id) = true) then failwith "Missing Id"
        let targetIndex = mapToShard document.Id flexIndex.Shards.Length
        let targetDocument = Generate document flexIndex.IndexSetting
        flexIndex.Shards.[targetIndex]
            .TrackingIndexWriter.updateDocument(new Term(Constants.IdField, document.Id), targetDocument)
    
    // Delete a flex document in an index    
    let Delete (id : string) flexIndex = 
        if (System.String.IsNullOrWhiteSpace(id) = true) then failwith "Missing Id"
        let targetIndex = mapToShard id flexIndex.Shards.Length
        flexIndex.Shards.[targetIndex].TrackingIndexWriter.deleteDocuments(new Term(Constants.IdField, id))

// ----------------------------------------------------------------------------
// Contains lucene writer IO and infracture related operations
// ----------------------------------------------------------------------------
[<AutoOpen>]
[<RequireQualifiedAccess>]
module IO = 
    // ----------------------------------------------------------------------------     
    // Creates lucene index writer config from flex index setting 
    // ---------------------------------------------------------------------------- 
    let private getIndexWriterConfig (flexIndexSetting : FlexIndexSetting) = 
        try 
            let iwc = new IndexWriterConfig(Constants.LuceneVersion, flexIndexSetting.IndexAnalyzer)
            iwc.setOpenMode (org.apache.lucene.index.IndexWriterConfig.OpenMode.CREATE_OR_APPEND) |> ignore
            iwc.setRAMBufferSizeMB (System.Double.Parse(flexIndexSetting.IndexConfiguration.RamBufferSizeMb.ToString())) 
            |> ignore
            Choice1Of2(iwc)
        with e -> 
            let error = InvalidOperation.WithDeveloperMessage(ExceptionConstants.ERROR_OPENING_INDEXWRITER, e.Message)
            Choice2Of2(error)
    
    // ----------------------------------------------------------------------------                  
    // Create a lucene filesystem lock over a directory    
    // ---------------------------------------------------------------------------- 
    let private getIndexDirectory (directoryPath : string) (directoryType : DirectoryType) = 
        // Note: Might move to SingleInstanceLockFactory to provide other services to open
        // the index in readonly mode
        let lockFactory = new NativeFSLockFactory()
        let file = new java.io.File(directoryPath)
        try 
            match directoryType with
            | DirectoryType.FileSystem -> 
                Choice1Of2(FSDirectory.``open`` (file, lockFactory) :> org.apache.lucene.store.Directory)
            | DirectoryType.MemoryMapped -> 
                Choice1Of2(MMapDirectory.``open`` (file, lockFactory) :> org.apache.lucene.store.Directory)
            | DirectoryType.Ram -> Choice1Of2(new RAMDirectory() :> org.apache.lucene.store.Directory)
            | _ -> 
                let error = 
                    InvalidOperation.WithDeveloperMessage
                        (ExceptionConstants.ERROR_OPENING_INDEXWRITER, "Unknown directory type.")
                Choice2Of2(error)
        with e -> 
            let error = InvalidOperation.WithDeveloperMessage(ExceptionConstants.ERROR_OPENING_INDEXWRITER, e.Message)
            Choice2Of2(error)
    
    // ---------------------------------------------------------------------------- 
    // Creates lucene index writer from flex index setting  
    // ----------------------------------------------------------------------------                    
    let GetIndexWriter(indexSetting : FlexIndexSetting, directoryPath : string) = maybe {
        let! iwc = getIndexWriterConfig indexSetting
        let! indexDirectory = getIndexDirectory directoryPath indexSetting.IndexConfiguration.DirectoryType
        let indexWriter = new IndexWriter(indexDirectory, iwc)
        let trackingIndexWriter = new TrackingIndexWriter(indexWriter)
        return! Choice1Of2(indexWriter, trackingIndexWriter)
        }

//[<AutoOpen>]
//[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
//[<RequireQualifiedAccess>]
//module FlexIndex = 
//    // ----------------------------------------------------------------------------   
//    // List of exceptions which can be thrown by manager
//    // ----------------------------------------------------------------------------   
//    let indexAlreadyExistsMessage = "The requested index already exist."
//    
//    exception IndexAlreadyExistsException of string
//    
//    let indexDoesNotExistMessage = "The requested index does not exist."
//    
//    exception IndexDoesNotExistException of string
//    
//    let indexIsOfflineMessage = "The index is offline or closing. Please bring the index online to use it."
//    
//    exception IndexIsOfflineException of string
//    
//    let indexIsOpeningMessage = "The index is in opening state. Please wait some time before making another request."
//    
//    exception IndexIsOpeningException of string
//    
//    let indexRegisterationMissingMessage = "Registeration information associated with the index is missing."
//    
//    exception IndexRegisterationMissingException of string
//    
//    // ----------------------------------------------------------------------------   
//    // Represents a dummy lucene document. There will be one per index stored in a
//    // dictionary
//    type threadLocalDocument = 
//        { Document : Document
//          FieldsLookup : Dictionary<string, Field>
//          LastGeneration : int }
//    
//    // ----------------------------------------------------------------------------   
//    // Concerete implementation of the index service interface. This class will be 
//    // injected using DI thus exposing the necessary
//    // functionality at any web service
//    // loadAllIndex - This is used to bypass loading of index at initialization time.
//    // Helpful for testing
//    // ----------------------------------------------------------------------------   
//    type IndexService(settingsParser : ISettingsBuilder, keyValueStore : IPersistanceStore, loadAllIndex : bool, versionCache : IVersioningCacheStore) = 
//        //let indexLogger = LogManager.GetLogger("IndexService")
//        // Dictionary to hold all the information about currently active index and their status
//        let indexRegisteration : ConcurrentDictionary<string, FlexIndex> = 
//            new ConcurrentDictionary<string, FlexIndex>(StringComparer.OrdinalIgnoreCase)
//        // Dictionary to hold the current status of the indices. This is a thread 
//        // safe dictionary so it is easier to update it compared to a
//        // mutable field on index setting 
//        let indexStatus : ConcurrentDictionary<string, IndexState> = 
//            new ConcurrentDictionary<string, IndexState>(StringComparer.OrdinalIgnoreCase)
//        // ----------------------------------------------------------------------------  
//        // For optimal indexing performance, re-use the Field and Document 
//        // instance for more than one document. But that is not easily possible
//        // in a multi-threaded scenario using TPL dataflow as we don't know which 
//        // thread it is using to execute each task. The easiest way
//        // is to use ThreadLocal value to create a local copy of the index document.
//        // The implication of creating one lucene document class per document to 
//        // be indexed is the penalty it has in terms of garbage collection. Also,
//        // lucene's document and index classes can't be shared across threads.
//        // ----------------------------------------------------------------------------          
//        let threadLocalStore : ThreadLocal<ConcurrentDictionary<string, threadLocalDocument>> = 
//            new ThreadLocal<ConcurrentDictionary<string, threadLocalDocument>>(fun () -> 
//            new ConcurrentDictionary<string, threadLocalDocument>(StringComparer.OrdinalIgnoreCase))
//        
//        // ----------------------------------------------------------------------------               
//        // Function to check if the requested index is available. If yes then tries to 
//        // retrieve the dcument template associated with the index from threadlocal store.
//        // If there is no template document for the requested index then goes ahead
//        // and creates one. 
//        // ----------------------------------------------------------------------------   
//        let indexExists (indexName) = 
//            match indexRegisteration.TryGetValue(indexName) with
//            | (true, flexIndex) -> 
//                match threadLocalStore.Value.TryGetValue(indexName) with
//                | (true, a) -> Some(flexIndex, a)
//                | _ -> 
//                    let luceneDocument = new Document()
//                    let fieldLookup = new Dictionary<string, Field>(StringComparer.OrdinalIgnoreCase)
//                    let idField = new StringField(Constants.IdField, "", Field.Store.YES)
//                    luceneDocument.add (idField)
//                    fieldLookup.Add(Constants.IdField, idField)
//                    let typeField = new StringField(Constants.TypeField, indexName, Field.Store.YES)
//                    luceneDocument.add (typeField)
//                    fieldLookup.Add(Constants.TypeField, typeField)
//                    let versionField = new IntField(Constants.VersionField, 0, Field.Store.YES)
//                    luceneDocument.add (typeField)
//                    fieldLookup.Add(Constants.VersionField, typeField)
//                    let lastModifiedField = 
//                        new LongField(Constants.LastModifiedField, GetCurrentTimeAsLong(), Field.Store.YES)
//                    luceneDocument.add (lastModifiedField)
//                    fieldLookup.Add(Constants.LastModifiedField, lastModifiedField)
//                    for field in flexIndex.IndexSetting.Fields do
//                        // Ignore these 3 fields here.
//                        if (field.FieldName = Constants.IdField || field.FieldName = Constants.TypeField 
//                            || field.FieldName = Constants.LastModifiedField || field.FieldName = Constants.VersionField) then 
//                            ()
//                        else 
//                            let defaultField = FlexField.CreateDefaultLuceneField field
//                            luceneDocument.add (defaultField)
//                            fieldLookup.Add(field.FieldName, defaultField)
//                    let documentTemplate = 
//                        { Document = luceneDocument
//                          FieldsLookup = fieldLookup
//                          LastGeneration = 0 }
//                    threadLocalStore.Value.TryAdd(indexName, documentTemplate) |> ignore
//                    Some(flexIndex, documentTemplate)
//            | _ -> None
//        
//        // ----------------------------------------------------------------------------     
//        // Updates the current thread local index document with the incoming data
//        // ----------------------------------------------------------------------------     
//        let UpdateDocument(flexIndex : FlexIndex, documentTemplate : threadLocalDocument, documentId : string, 
//                           version : int, fields : Dictionary<string, string>) = 
//            documentTemplate.FieldsLookup.[Constants.IdField].setStringValue(documentId)
//            documentTemplate.FieldsLookup.[Constants.LastModifiedField].setLongValue(GetCurrentTimeAsLong())
//            documentTemplate.FieldsLookup.[Constants.VersionField].setIntValue(version)
//            for field in flexIndex.IndexSetting.Fields do
//                // Ignore these 3 fields here.
//                if (field.FieldName = Constants.IdField || field.FieldName = Constants.TypeField 
//                    || field.FieldName = Constants.LastModifiedField || field.FieldName = Constants.VersionField) then 
//                    ()
//                else 
//                    // If it is computed field then generate and add it otherwise follow standard path
//                    match field.Source with
//                    | Some(s) -> 
//                        try 
//                            // Wrong values for the data type will still be handled as update lucene field will
//                            // check the data type
//                            let value = s fields
//                            FlexField.UpdateLuceneField field documentTemplate.FieldsLookup.[field.FieldName] value
//                        with e -> 
//                            FlexField.UpdateLuceneFieldToDefault field documentTemplate.FieldsLookup.[field.FieldName]
//                    | None -> 
//                        match fields.TryGetValue(field.FieldName) with
//                        | (true, value) -> 
//                            FlexField.UpdateLuceneField field documentTemplate.FieldsLookup.[field.FieldName] value
//                        | _ -> 
//                            FlexField.UpdateLuceneFieldToDefault field documentTemplate.FieldsLookup.[field.FieldName]
//            let targetIndex = 
//                if (flexIndex.Shards.Length = 1) then 0
//                else Document.mapToShard documentId flexIndex.Shards.Length
//            (flexIndex, targetIndex, documentTemplate)
//        
//        // ----------------------------------------------------------------------------     
//        // Function to process the 
//        // ----------------------------------------------------------------------------                                         
//        let processItem (indexMessage : IndexCommand, flexIndex : FlexIndex, versionCache : IVersioningCacheStore) = 
//            match indexMessage with
//            | Create(documentId, fields) -> 
//                match indexExists (flexIndex.IndexSetting.IndexName) with
//                | Some(flexIndex, documentTemplate) -> 
//                    let (flexIndex, targetIndex, documentTemplate) = 
//                        UpdateDocument(flexIndex, documentTemplate, documentId, 1, fields)
//                    versionCache.AddVersion flexIndex.IndexSetting.IndexName documentId 1 |> ignore
//                    flexIndex.Shards.[targetIndex].TrackingIndexWriter.addDocument(documentTemplate.Document) |> ignore
//                    (true, "")
//                | _ -> (false, "Index does not exist")
//            | Update(documentId, fields) -> 
//                match indexExists (flexIndex.IndexSetting.IndexName) with
//                | Some(flexIndex, documentTemplate) -> 
//                    // It is a simple update so get the version number and increment it
//                    match versionCache.GetVersion flexIndex.IndexSetting.IndexName documentId with
//                    | Some(x) -> 
//                        let (version, dateTime) = x
//                        match versionCache.UpdateVersion flexIndex.IndexSetting.IndexName documentId version dateTime 
//                                  (version + 1) with
//                        | true -> 
//                            // Version was updated successfully so let's update the document
//                            let (flexIndex, targetIndex, documentTemplate) = 
//                                UpdateDocument(flexIndex, documentTemplate, documentId, (version + 1), fields)
//                            flexIndex.Shards.[targetIndex]
//                                .TrackingIndexWriter.updateDocument(new Term("id", documentId), 
//                                                                    documentTemplate.Document) |> ignore
//                        | false -> failwithf "Version mismatch"
//                    | None -> 
//                        // Document was not found in version cache so retrieve it from the index
//                        let (flexIndex, targetIndex, documentTemplate) = 
//                            UpdateDocument(flexIndex, documentTemplate, documentId, 1, fields)
//                        let query = new TermQuery(new Term("id", documentId))
//                        let searcher = 
//                            (flexIndex.Shards.[targetIndex].NRTManager :> ReferenceManager).acquire() :?> IndexSearcher
//                        let topDocs = searcher.search (query, 1)
//                        let hits = topDocs.scoreDocs
//                        if hits.Length = 0 then 
//                            // It is actually a create as the document does not exist
//                            versionCache.AddVersion flexIndex.IndexSetting.IndexName documentId 1 |> ignore
//                            flexIndex.Shards.[targetIndex].TrackingIndexWriter.addDocument(documentTemplate.Document) 
//                            |> ignore
//                        else 
//                            let version = int (searcher.doc(hits.[0].doc).get("version"))
//                            versionCache.AddVersion flexIndex.IndexSetting.IndexName documentId (version + 1) |> ignore
//                            flexIndex.Shards.[targetIndex].TrackingIndexWriter.addDocument(documentTemplate.Document) 
//                            |> ignore
//                    (true, "")
//                | _ -> (false, "Index does not exist")
//            | Delete(documentId) -> 
//                let targetIndex = Document.mapToShard documentId flexIndex.Shards.Length - 1
//                versionCache.DeleteVersion flexIndex.IndexSetting.IndexName documentId |> ignore
//                flexIndex.Shards.[targetIndex].TrackingIndexWriter.deleteDocuments(new Term("id", documentId)) |> ignore
//                (true, "")
//            | BulkDeleteByIndexName -> 
//                for shard in flexIndex.Shards do
//                    shard.TrackingIndexWriter.deleteAll() |> ignore
//                (true, "")
//            | Commit -> 
//                for i in 0..flexIndex.Shards.Length - 1 do
//                    flexIndex.Shards.[i].IndexWriter.commit()
//                (true, "")
//        
//        // Default buffering queue
//        // This is TPL Dataflow based approach. Can replace it with parallel.foreach
//        // on blocking collection. 
//        // Advantages - Faster, EnumerablePartitionerOptions.NoBuffering takes care of the
//        // older .net partitioner bug, Can reduce the number of lucene documents which will be
//        // generated 
//        let mutable queue : ActionBlock<string * IndexCommand> = null
//        
//        // Index auto commit changes job
//        let commitJob (flexIndex : FlexIndex) = 
//            // Looping over array by index number is usually the fastest
//            // iteration method
//            for i in 0..flexIndex.Shards.Length - 1 do
//                // Lucene 4.4.0 feature to check for uncommitted changes
//                if flexIndex.Shards.[i].IndexWriter.hasUncommittedChanges() then 
//                    flexIndex.Shards.[i].IndexWriter.commit()
//        
//        // Index auto commit changes job
//        let refreshIndexJob (flexIndex) = 
//            // Looping over array by index number is usually the fastest
//            // iteration method
//            for i in 0..flexIndex.Shards.Length - 1 do
//                flexIndex.Shards.[i].NRTManager.maybeRefresh() |> ignore
//        
//        // Creates a async timer which can be used to execute a funtion at specified
//        // period of time. This is used to schedule all recurring indexing tasks
//        let ScheduleIndexJob delay (work : FlexIndex -> unit) flexIndex = 
//            let rec loop time (cts : CancellationTokenSource) = 
//                async { 
//                    do! Async.Sleep(time)
//                    if (cts.IsCancellationRequested) then cts.Dispose()
//                    else work (flexIndex)
//                    return! loop delay cts
//                }
//            loop delay flexIndex.Token
//        
//        // Add index to the registeration
//        let addIndex (flexIndexSetting : FlexIndexSetting) = 
//            // Add index status
//            indexStatus.TryAdd(flexIndexSetting.IndexName, IndexState.Opening) |> ignore
//            // Initialize shards
//            let shards = 
//                Array.init flexIndexSetting.ShardConfiguration.ShardCount (fun a -> 
//                    let writers = 
//                        GetIndexWriter(flexIndexSetting, flexIndexSetting.BaseFolder + "\\shards\\" + a.ToString())
//                    if writers.IsNone then 
//                        //logger.Error("Unable to create the requested index writer.")
//                        failwith "Unable to create the requested index writer."
//                    let (indexWriter, trackingIndexWriter) = writers.Value
//                    // Based on Lucene 4.4 the nrtmanager is replaced with ControlledRealTimeReopenThread which can take any
//                    // reference manager
//                    let nrtManager = new SearcherManager(indexWriter, true, new SearcherFactory())
//                    
//                    let shard = 
//                        { ShardNumber = a
//                          NRTManager = nrtManager
//                          ReopenThread = 
//                              new ControlledRealTimeReopenThread(trackingIndexWriter, nrtManager, float (25), float (5))
//                          IndexWriter = indexWriter
//                          TrackingIndexWriter = trackingIndexWriter }
//                    shard)
//            
//            let flexIndex = 
//                { IndexSetting = flexIndexSetting
//                  Shards = shards
//                  Token = new System.Threading.CancellationTokenSource() }
//            
//            // Add the scheduler for the index
//            // Commit Scheduler
//            Async.Start(ScheduleIndexJob (flexIndexSetting.IndexConfiguration.CommitTimeSec * 1000) commitJob flexIndex)
//            // NRT Scheduler
//            Async.Start
//                (ScheduleIndexJob flexIndexSetting.IndexConfiguration.RefreshTimeMilliSec refreshIndexJob flexIndex)
//            // Add the index to the registeration
//            indexRegisteration.TryAdd(flexIndexSetting.IndexName, flexIndex) |> ignore
//            indexStatus.[flexIndex.IndexSetting.IndexName] <- IndexState.Online
//        
//        // ----------------------------------------------------------------------------
//        // Close an open index
//        // ----------------------------------------------------------------------------
//        let closeIndex (flexIndex : FlexIndex) = 
//            try 
//                indexRegisteration.TryRemove(flexIndex.IndexSetting.IndexName) |> ignore
//                // Update status from online to closing
//                indexStatus.[flexIndex.IndexSetting.IndexName] <- IndexState.Closing
//                flexIndex.Token.Cancel()
//                flexIndex.Shards |> Array.iter (fun x -> 
//                                        x.NRTManager.close()
//                                        x.IndexWriter.commit()
//                                        x.IndexWriter.close())
//            with e -> () //logger.Error("Error while closing index:" + flexIndex.IndexSetting.IndexName, e)
//            indexStatus.[flexIndex.IndexSetting.IndexName] <- IndexState.Offline
//        
//        // ----------------------------------------------------------------------------
//        // Utility method to return index registeration information
//        // ----------------------------------------------------------------------------
//        let getIndexRegisteration (indexName) = 
//            match indexStatus.TryGetValue(indexName) with
//            | (true, status) -> 
//                match status with
//                | IndexState.Online -> 
//                    match indexRegisteration.TryGetValue(indexName) with
//                    | (true, flexIndex) -> flexIndex
//                    | _ -> raise (IndexRegisterationMissingException indexRegisterationMissingMessage)
//                | IndexState.Opening -> raise (IndexIsOpeningException indexIsOpeningMessage)
//                | IndexState.Offline | IndexState.Closing -> raise (IndexIsOfflineException indexIsOfflineMessage)
//            | _ -> raise (IndexDoesNotExistException indexDoesNotExistMessage)
//        
//        // Process index queue requests
//        let processQueueItem (indexName, indexMessage : IndexCommand) = 
//            let flexIndex = getIndexRegisteration (indexName)
//            processItem (indexMessage, flexIndex, versionCache) |> ignore
//        
//        // ----------------------------------------------------------------------------
//        // Load all index configuration data on start of application
//        // ----------------------------------------------------------------------------
//        do 
//            //indexLogger.Info("Index loading: Operation Start")
//            let executionBlockOption = new ExecutionDataflowBlockOptions()
//            executionBlockOption.MaxDegreeOfParallelism <- -1
//            executionBlockOption.BoundedCapacity <- 1000
//            queue <- new ActionBlock<string * IndexCommand>(processQueueItem, executionBlockOption)
//            if loadAllIndex then 
//                keyValueStore.GetAll<Index>() |> Seq.iter (fun x -> 
//                                                     if x.Online then 
//                                                         try 
//                                                             let flexIndexSetting = settingsParser.BuildSetting(x)
//                                                             addIndex (flexIndexSetting)
//                                                         //indexLogger.Info(sprintf "Index: %s loaded successfully." x.IndexName)
//                                                         with ex -> ()
//                                                     //indexLogger.Error("Loading index from file failed.", ex)
//                                                     else 
//                                                         //indexLogger.Info(sprintf "Index: %s is not loaded as it is set to be offline." x.IndexName)
//                                                         indexStatus.TryAdd(x.IndexName, IndexState.Offline) |> ignore)
//            else ()
//        
//        //indexLogger.Info("Index loading bypassed. LoadIndex parameter is false. (Testing mode)")
//        // ----------------------------------------------------------------------------
//        // Interface implementation
//        // ----------------------------------------------------------------------------
//        interface IIndexService with
//            
//            member this.PerformCommandAsync(indexName, indexMessage, replyChannel) = 
//                let flexIndex = getIndexRegisteration (indexName)
//                replyChannel.Reply(processItem (indexMessage, flexIndex, versionCache))
//            
//            member this.PerformCommand(indexName, indexMessage) = 
//                let flexIndex = getIndexRegisteration (indexName)
//                processItem (indexMessage, flexIndex, versionCache)
//            
//            member this.CommandQueue() = queue
//            
//            //            member this.PerformQuery(indexName, indexQuery) =
//            //                let flexIndex = getIndexRegisteration(indexName)  
//            //                match indexQuery with
//            //                | SearchQuery(a) -> searchService.Search(flexIndex, a)
//            //                | SearchProfileQuery(a) -> searchService.SearchProfile(flexIndex, a)
//            //            member this.PerformQueryAsync(indexName, indexQuery, replyChannel) =
//            //                let flexIndex = getIndexRegisteration(indexName)     
//            //                ()      
//            //                match indexQuery with
//            //                | SearchQuery(a) -> replyChannel.Reply(searchService.Search(flexIndex, a))
//            //                | SearchProfileQuery(a) -> replyChannel.Reply(searchService.SearchProfile(flexIndex, a))
//            member this.IndexExists(indexName) = 
//                match indexStatus.TryGetValue(indexName) with
//                | (true, _) -> true
//                | _ -> false
//            
//            member this.IndexStatus(indexName) = 
//                match indexStatus.TryGetValue(indexName) with
//                | (true, status) -> status
//                | _ -> raise (IndexDoesNotExistException indexDoesNotExistMessage)
//            
//            member this.GetIndex indexName = 
//                match indexStatus.TryGetValue(indexName) with
//                | (true, _) -> 
//                    match keyValueStore.Get<Index>(indexName) with
//                    | Some(a) -> a
//                    | None -> raise (IndexDoesNotExistException indexDoesNotExistMessage)
//                | _ -> raise (IndexDoesNotExistException indexDoesNotExistMessage)
//            
//            member this.AddIndex flexIndex = 
//                match indexStatus.TryGetValue(flexIndex.IndexName) with
//                | (true, _) -> raise (IndexAlreadyExistsException indexAlreadyExistsMessage)
//                | _ -> 
//                    let settings = settingsParser.BuildSetting(flexIndex)
//                    keyValueStore.Put flexIndex.IndexName flexIndex |> ignore
//                    if flexIndex.Online then addIndex (settings)
//                    else indexStatus.TryAdd(flexIndex.IndexName, IndexState.Offline) |> ignore
//                ()
//            
//            member this.UpdateIndex index = 
//                match indexStatus.TryGetValue(index.IndexName) with
//                | (true, status) -> 
//                    match status with
//                    | IndexState.Online -> 
//                        match indexRegisteration.TryGetValue(index.IndexName) with
//                        | (true, flexIndex) -> 
//                            let settings = settingsParser.BuildSetting(index)
//                            closeIndex (flexIndex)
//                            addIndex (settings)
//                            keyValueStore.Put index.IndexName index |> ignore
//                        | _ -> raise (IndexRegisterationMissingException indexRegisterationMissingMessage)
//                    | IndexState.Opening -> raise (IndexIsOpeningException indexIsOpeningMessage)
//                    | IndexState.Offline | IndexState.Closing -> 
//                        let settings = settingsParser.BuildSetting(index)
//                        keyValueStore.Put index.IndexName index |> ignore
//                | _ -> raise (IndexDoesNotExistException indexDoesNotExistMessage)
//            
//            member this.DeleteIndex indexName = 
//                match indexStatus.TryGetValue(indexName) with
//                | (true, status) -> 
//                    match status with
//                    | IndexState.Online -> 
//                        match indexRegisteration.TryGetValue(indexName) with
//                        | (true, flexIndex) -> 
//                            closeIndex (flexIndex)
//                            keyValueStore.Delete<Index> indexName |> ignore
//                            // It is possible that directory might not exist if the index has never been opened
//                            if Directory.Exists(Constants.DataFolder.Value + "\\" + indexName) then 
//                                Directory.Delete(flexIndex.IndexSetting.BaseFolder, true)
//                        | _ -> raise (IndexRegisterationMissingException indexRegisterationMissingMessage)
//                    | IndexState.Opening -> raise (IndexIsOpeningException indexIsOpeningMessage)
//                    | IndexState.Offline | IndexState.Closing -> 
//                        keyValueStore.Delete<Index> indexName |> ignore
//                        // It is possible that directory might not exist if the index has never been opened
//                        if Directory.Exists(Constants.DataFolder.Value + "\\" + indexName) then 
//                            Directory.Delete(Constants.DataFolder.Value + "\\" + indexName, true)
//                | _ -> raise (IndexDoesNotExistException indexDoesNotExistMessage)
//            
//            member this.CloseIndex indexName = 
//                let flexIndex = getIndexRegisteration (indexName)
//                closeIndex (flexIndex)
//                let index = keyValueStore.Get<Index>(indexName)
//                index.Value.Online <- false
//                keyValueStore.Put indexName index.Value |> ignore
//            
//            member this.OpenIndex indexName = 
//                match indexStatus.TryGetValue(indexName) with
//                | (true, status) -> 
//                    match status with
//                    | IndexState.Online | IndexState.Opening -> raise (IndexIsOpeningException indexIsOpeningMessage)
//                    | IndexState.Offline | IndexState.Closing -> 
//                        let index = keyValueStore.Get<Index>(indexName)
//                        let settings = settingsParser.BuildSetting(index.Value)
//                        let res = addIndex (settings)
//                        index.Value.Online <- true
//                        keyValueStore.Put indexName index.Value |> ignore
//                | _ -> raise (IndexDoesNotExistException indexDoesNotExistMessage)
//            
//            member this.ShutDown() = 
//                for index in indexRegisteration do
//                    closeIndex (index.Value)
//                true