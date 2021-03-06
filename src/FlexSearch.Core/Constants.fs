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

open FlexSearch.Utility

open java.io
open java.util

open org.apache.lucene.analysis
open org.apache.lucene.analysis.core
open org.apache.lucene.analysis.util
open org.apache.lucene.analysis.miscellaneous
open org.apache.lucene.codecs
open org.apache.lucene.codecs.lucene42
open org.apache.lucene.document
open org.apache.lucene.facet.search
open org.apache.lucene.index
open org.apache.lucene.search
open org.apache.lucene.store

open System
open System.ComponentModel.Composition
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Threading


// ----------------------------------------------------------------------------
// Contains all the flex constants and cache store definitions 
// ----------------------------------------------------------------------------
[<AutoOpen>]
[<RequireQualifiedAccess>]
module Constants =

    // Lucene version to be used across the application
    let LuceneVersion = org.apache.lucene.util.Version.LUCENE_45
    let IdField = "id"
    let LastModifiedField = "lastmodified"
    let TypeField = "type"
    let VersionField = "version"

    // Flex root folder path
    let private rootFolder = lazy AppDomain.CurrentDomain.SetupInformation.ApplicationBase

    // Flex data folder
    let DataFolder = lazy (
        match Directory.Exists(rootFolder.Force() + "\\Data\\") with
        | true -> rootFolder.Force() + "\\Data\\"
        | _ -> failwithf "Terminating Lunar due to a fatal error. Root cause: 'Data' Indices configuration directory does not exist at the root location.; rootFolder=%s" rootFolder.Value
        )
    

    // Flex index folder
    let ConfFolder = lazy (
        match Directory.Exists(rootFolder.Force() + "\\Conf\\") with
        | true -> rootFolder.Force() + "\\Conf\\"
        | _ -> failwithf "message=Terminating Lunar due to a fatal error.; cause='\\Conf\\' configuration directory does not exist at the root location.; rootFolder=%s" rootFolder.Value
        )
    
     
    // Flex plugins folder
    let PluginFolder = lazy (
        match Directory.Exists(rootFolder.Force() + "\\Plugins") with
        | true -> rootFolder.Force() + "\\Plugins"
        | _ -> failwithf "Terminating Lunar due to a fatal error. Root cause: '\\Plugins' Plugin directory does not exist at the root location.; rootFolder=%s" rootFolder.Value
        )
       


