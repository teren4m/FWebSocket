namespace WebSocket

open System
open System.IO
open System.Text
open System.Security.Cryptography

type OpCode = Text=0x1uy | Pong=0xAuy

type Msg =
    | Connect of MailboxProcessor<byte array>
    | Disconnect of MailboxProcessor<byte array>
    | Data of byte array

module ServerUtil =

  let pingInterval = 3000

  let guid6455 = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"

  /// headers can occur in any order (See 1.3 in [1])
  let isWebSocketsUpgrade (lines: string array) = 
    [| "GET /echo HTTP/1.1"; "Upgrade: websocket"; "Connection: Upgrade"|] 
    |> Array.map(
        fun x->lines |> Array.exists(fun y->x.ToLower()=y.ToLower()))
    |> Array.reduce(fun x y->x && y)
  
  /// see: [1]
  let calcWSAccept6455 (secWebSocketKey:string) = 
    let ck = secWebSocketKey + guid6455
    let sha1 = SHA1CryptoServiceProvider.Create()
    let hashBytes = ck |> Encoding.ASCII.GetBytes |> sha1.ComputeHash
    let Sec_WebSocket_Accept = hashBytes |> Convert.ToBase64String
    Sec_WebSocket_Accept

  let createAcceptString6455 acceptCode = 
    "HTTP/1.1 101 Switching Protocols\r\n" +
    "Upgrade: websocket\r\n" +
    "Connection: Upgrade\r\n" +
    "Sec-WebSocket-Accept: " + acceptCode + "\r\n" 
    + "\r\n"

  let getKey key arr = 
      try 
        let item = (Array.find (fun (s:String) -> s.StartsWith(key)) arr)
        item.Substring key.Length
      with
        | _ -> ""

  let startMailboxProcessor ct f = MailboxProcessor.Start(f, cancellationToken = ct)

  let getOpCodeFromByte (x : byte) : Option<OpCode> = 
    if Enum.IsDefined (typeof<OpCode>, x) then Some (LanguagePrimitives.EnumOfValue x) else None

  let getTextByMask (mask: byte[]) = 
      Seq.mapi (fun i x -> Convert.ToChar(x ^^^ mask.[i % mask.Length])) >> String.Concat
  
  let isInvert x = (0x80uy &&& x) = 0x80uy

  let getMask (bytes: byte[]): Option<byte array> = 
      if isInvert bytes.[1] then Some bytes.[2..5] else None

  let makeFrameShortTxt (P:byte array) =
    let message = new MemoryStream()
    try                         
      message.WriteByte(byte 0x81)                              // see 5.6 & 11.8 in [1].  See [2], and [3]
      message.WriteByte(byte P.Length)                          // assume length < 127 bytes.  as yet, no implementation of masking, extended payload length, or the mask
      message.Write(P,0,P.Length)                               // TODO: cobine with [8]
      message.ToArray()
    finally
      message.Close()

  let makeFramePing =
    let message = new MemoryStream()
    try                         
      message.WriteByte(byte 0x89)                              
      message.WriteByte(byte 0)                                 
      message.ToArray()
    finally
      message.Close()