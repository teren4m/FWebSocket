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
      WebSocketServer.start(ipAddress, port)
      0 