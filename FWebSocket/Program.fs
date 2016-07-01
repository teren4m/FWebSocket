namespace WebSocket

// Appache 2.0 license

// References:
// [1] Proposed WebSockets Spec December 2011         http://tools.ietf.org/html/rfc6455   
// [2] John McCutchan (Google Dart Team Member)       http://www.altdevblogaday.com/2012/01/23/writing-your-own-websocket-server/
// [3] A pretty good Python implemenation by mrrrgn   https://github.com/mrrrgn/websocket-data-frame-encoder-decoder/blob/master/frame.py
// [4] WebSockets Organising body                     http://www.websocket.org/echo.html
// [5] AndrewNewcomb's Gist (starting point)          https://gist.github.com/AndrewNewcomb/711664
// [6] C# version (Vladimir's starting point?)        http://nugget.codeplex.com
// [7] VLADIMIR MATVEEV                               http://v2matveev.blogspot.com/2010/04/mailboxprocessors-practical-application.html
// [8] Matt Moloney's Frame Implementation            https://gist.github.com/moloneymb/18959a56c10beb907608

// Disclaimer: very early stages of refactoring + still hacky

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open System.Runtime.Serialization
open System.Security.Cryptography

module ServerUtil =

  let guid6455 = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"

  /// headers can occur in any order (See 1.3 in [1])
  let isWebSocketsUpgrade (lines: string array) = 
    [| "GET /Time/ HTTP/1.1"; "Upgrade: websocket"; "Connection: Upgrade"|] 
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

  let makeFrame_ShortTxt (P:byte array) =
    let message = new MemoryStream()
    try                         
      message.WriteByte(byte 0x81)                              // see 5.6 & 11.8 in [1].  See [2], and [3]
      message.WriteByte(byte P.Length)                          // assume length < 127 bytes.  as yet, no implementation of masking, extended payload length, or the mask
      message.Write(P,0,P.Length)                               // TODO: cobine with [8]
      message.ToArray()
    finally
      message.Close()


open ServerUtil

[<DataContract>]
type Time =
    { [<DataMember(Name = "hour")>] mutable Hour : int
      [<DataMember(Name = "minute")>] mutable Minute : int
      [<DataMember(Name = "second")>] mutable Second : int }
    static member New(dt : DateTime) = {Hour = dt.Hour; Minute = dt.Minute; Second = dt.Second}
 
type Msg =
    | Connect of MailboxProcessor<Time>
    | Disconnect of MailboxProcessor<Time>
    | Tick of Time
 
module WebSocketServer =

  let port = 3002
  let ipAddress = IPAddress.Loopback.ToString()
  let origin = "127.0.0.1"
 
  let startMailboxProcessor ct f = MailboxProcessor.Start(f, cancellationToken = ct)
 
  let timer (ctrl : MailboxProcessor<Msg>) interval = async {
      while true do
          do! Async.Sleep interval
          ctrl.Post(Tick <| Time.New(DateTime.Now))            
      }
 
  let runController (ct : CancellationToken) = 
      startMailboxProcessor ct (fun (inbox : MailboxProcessor<Msg>) ->
          let listeners = new ResizeArray<_>()
          async {
              while not ct.IsCancellationRequested do
                  let! msg = inbox.Receive()
                  match msg with
                  | Connect l -> 
                      Console.WriteLine "Connect"
                      listeners.Add(l)
                  | Disconnect l -> 
                      Console.WriteLine "Disconnect"
                      listeners.Remove(l) |> ignore
                  | Tick msg -> listeners.ForEach(fun l -> l.Post msg)
          }
      )

  let runWorker (tcp : TcpClient) (ctrl : MailboxProcessor<Msg>) ct = 
      
      ignore <| startMailboxProcessor ct (fun (inbox : MailboxProcessor<Time>) ->
          let rec handshake = async {            
              let ns = tcp.GetStream()

              let bytes = Array.create tcp.ReceiveBufferSize (byte 0)
              let bytesReadCount = ns.Read (bytes, 0, bytes.Length)

              if bytesReadCount > 8 then                
                  let lines = bytes.[..(bytesReadCount-9)]
                              |> System.Text.UTF8Encoding.UTF8.GetString 
                              |> fun hs->hs.Split([|"\r\n"|], StringSplitOptions.RemoveEmptyEntries) 
                  match isWebSocketsUpgrade lines with
                  | true ->
                      let acceptStr =  
                                      (getKey "Sec-WebSocket-Key:" lines                 
                                      ).Substring(1)                                // skip space
                                      |> calcWSAccept6455 
                                      |> createAcceptString6455
                      Console.WriteLine acceptStr                                   // debug
                      do! ns.AsyncWrite <| Encoding.ASCII.GetBytes acceptStr
                      return! run ns tcp.ReceiveBufferSize
                  | _ ->                      
                      tcp.Close()                                                   // validation failed - close connection
              else
                  tcp.Close()                                                       // validation failed - close connection
              }
          and run (ns : NetworkStream) size = async {
              let json = System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof<Time>)
              ctrl.Post(Connect inbox)

              try
                  while not ct.IsCancellationRequested do
                      let! time = inbox.Receive()

                      let payload = new MemoryStream()
                      json.WriteObject(payload, time)

                      let df = makeFrame_ShortTxt <| payload.ToArray()
                      ns.Write(df,0,df.Length)

              finally
                  ns.Close()
                  ctrl.Post(Disconnect inbox)
          }



          handshake
     ) 

  let readStream (tcp : TcpClient) = async{
        while true do
          Console.WriteLine("-------------------------------");
          let bytes = Array.create tcp.ReceiveBufferSize (byte 0)
          let bytesReadCount = tcp.GetStream().Read (bytes, 0, bytes.Length)
          let result = System.Text.Encoding.UTF8.GetString(bytes);
          Console.WriteLine(result);
          Console.WriteLine("-------------------------------");
      }

 
  let runRequestDispatcher () =
      let listener = new TcpListener(IPAddress.Parse(ipAddress), port)
      let cts = new CancellationTokenSource()
      let token = cts.Token
 
      let controller = runController token
      Async.Start (timer controller 1000, token)
 
      let main = async {
          try
              listener.Start()
              while not cts.IsCancellationRequested do
                  let! client = Async.FromBeginEnd(listener.BeginAcceptTcpClient, listener.EndAcceptTcpClient)
                  Async.Start (readStream client, token)
                  //client.NoDelay <- true
                  runWorker client controller token
          finally
              listener.Stop()       
      }
 
      Async.Start(main, token)
 
      { new IDisposable with member x.Dispose() = cts.Cancel()}

  let start() = 
    let dispose = runRequestDispatcher ()
    printfn "press any key to stop..."
    Console.ReadKey() |> ignore
    dispose.Dispose()

module Program = 

  [<EntryPoint>]
  let main argv = 
      //System.Threading.Server.runRequestDispatcher()
      WebSocketServer.start()
      0 // return an integer exit code