namespace WebSocket

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open System.Runtime.Serialization
 
module WebSocketServer =  

  let whileCycle isNext f = while isNext do f()
  let whileTrueCycle = whileCycle true
  let whileCycleAsync isNext f = async { whileCycle isNext f }
  let whileTrueCycleAsync f interval = async { 
      do! Async.Sleep interval
      f()
  }

  let post<'T> (ctrl : MailboxProcessor<'T>) data = fun () -> ctrl.Post(data)

  let ping mailBox interval = 
      let pingData = ServerUtil.makeFramePing
      let postPing = post<Msg> mailBox (Data pingData)
      whileTrueCycleAsync postPing interval
  
  let getText (bytes: byte[]) = 
      match ServerUtil.getMask bytes with
      | Some mask -> 
          let lastIndex = int ((0x7Fuy &&& bytes.[1]) + 5uy)
          bytes.[6..lastIndex] |> ServerUtil.getTextByMask mask
      | None -> 
          let lastIndex = int ((0x7Fuy &&& bytes.[1]) + 2uy)
          bytes.[2..lastIndex] |> System.Text.Encoding.ASCII.GetString

  let getData (ns : NetworkStream) size = async {
      while true do
          let bytes = Array.create size (byte 0)
          let bytesReadCount = ns.Read (bytes, 0, bytes.Length)
          let opcodeByte = 0x0Fuy &&& bytes.[0]
          
          match ServerUtil.getOpCodeFromByte opcodeByte with
          | Some opcode -> 
              match opcode with
              | OpCode.Pong -> ()
              | OpCode.Text -> printf "Data: %s" (getText bytes)
          | None -> ()
      }
 
  let runController (ct : CancellationToken) = 
      ServerUtil.startMailboxProcessor ct (fun (inbox : MailboxProcessor<Msg>) ->
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
                  | Data data -> listeners.ForEach(fun l -> l.Post data)
          }
      )

  let runWorker (tcp : TcpClient) (ctrl : MailboxProcessor<Msg>) ct = 
      ignore <| ServerUtil.startMailboxProcessor ct (fun (inbox : MailboxProcessor<byte array>) ->
          let rec handshake = async {            
              let ns = tcp.GetStream()

              let bytes = Array.create tcp.ReceiveBufferSize (byte 0)
              let bytesReadCount = ns.Read (bytes, 0, bytes.Length)

              Console.WriteLine("-------------------------------");
              let result = System.Text.Encoding.UTF8.GetString(bytes);
              Console.WriteLine(result);
              Console.WriteLine("-------------------------------");

              if bytesReadCount > 8 then                
                  let lines = bytes.[..(bytesReadCount-9)]
                              |> System.Text.UTF8Encoding.UTF8.GetString 
                              |> fun hs->hs.Split([|"\r\n"|], StringSplitOptions.RemoveEmptyEntries) 
                  match ServerUtil.isWebSocketsUpgrade lines with
                  | true ->
                      let acceptStr =  
                                      (ServerUtil.getKey "Sec-WebSocket-Key:" lines                 
                                      ).Substring(1)                                
                                      |> ServerUtil.calcWSAccept6455 
                                      |> ServerUtil.createAcceptString6455
                      Console.WriteLine acceptStr                                   
                      do! ns.AsyncWrite <| Encoding.ASCII.GetBytes acceptStr
                      return! run ns tcp.ReceiveBufferSize
                  | _ ->                      
                      tcp.Close()                                                   
              else
                  tcp.Close()                                                       
              }
          and run (ns : NetworkStream) size = async {
              ctrl.Post(Connect inbox)
              Async.Start (getData ns size, ct)

              try
                  while not ct.IsCancellationRequested do
                      let! time = inbox.Receive()
                      let df = ServerUtil.makeFramePing
                      ns.Write(df,0,df.Length)

              finally
                  ns.Close()
                  ctrl.Post(Disconnect inbox)
          }

          handshake
     ) 

 
  let runRequestDispatcher (ipAddress, port) =
      let listener = new TcpListener(IPAddress.Parse(ipAddress), port)
      let cts = new CancellationTokenSource()
      let token = cts.Token
 
      let controller = runController token
      Async.Start (ping controller ServerUtil.pingInterval, token)
 
      let main = async {
          try
              listener.Start()
              while not cts.IsCancellationRequested do
                  let! client = Async.FromBeginEnd(listener.BeginAcceptTcpClient, listener.EndAcceptTcpClient)
                  runWorker client controller token
          finally
              listener.Stop()       
      }
 
      Async.Start(main, token)
 
      { new IDisposable with member x.Dispose() = cts.Cancel()}

  let start (ipAddress, port): IDisposable = 
    runRequestDispatcher (ipAddress, port)
