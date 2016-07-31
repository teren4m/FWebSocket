namespace WebSocket

open System
open System.IO
open System.Net
open System.Net.Sockets
open System.Text
open System.Threading
open System.Security.Cryptography

module Program = 

  let port = 3002
  let ipAddress = IPAddress.Loopback.ToString()

  [<EntryPoint>]
  let main argv = 
      let dispose = WebSocketServer.start(ipAddress, port)
      printfn "press any key to stop..."
      Console.ReadKey() |> ignore
      dispose.Dispose()
      0 