namespace FSharpTypes

open System

type FSharpGuidId = FSharpGuidId of Guid

type FSharpStringId = FSharpStringId of string

type FSharpIntId = FSharpIntId of int

/// An ordinary F# class type — deliberately NOT [<AllowNullLiteral>], mirroring how a user
/// would actually write an F# Wolverine saga. Generated null guards over this type must
/// compile without the attribute (jasperfx#513).
type FSharpSaga() =
    member val Name = "" with get, set
