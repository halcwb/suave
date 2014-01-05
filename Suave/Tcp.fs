﻿module Suave.Tcp

open System
open System.Net
open System.Net.Sockets

/// The max backlog of number of requests
let MAX_BACK_LOG = Int32.MaxValue

type StartedData =
  { start_called_utc : DateTime
  ; socket_bound_utc : DateTime option
  ; source_ip        : IPAddress
  ; source_port      : uint16 }
  override x.ToString() =
    sprintf "started %s <-> %s : %O:%d"
      (x.start_called_utc.ToString("o"))
      (x.socket_bound_utc |> Option.fold (fun _ t -> t.ToString("o")) "x")
      x.source_ip x.source_port

/// Asynchronous extension methods to TcpListener to make
/// it nicer to consume in F#
type TcpListener with
  member x.AsyncAcceptTcpClient() =
    Async.FromBeginEnd(x.BeginAcceptTcpClient, x.EndAcceptTcpClient)

/// A TCP Worker is a thing that takes a TCP client and returns an asynchronous workflow thereof
type TcpWorker<'a> = TcpClient -> Async<'a>

/// Close the TCP client by closing its stream and then closing the client itself
let close (d : TcpClient) =
  if d.Connected then
    try // we race the network to act on the stream/client, so swallow any errors
      d.GetStream().Close()
      d.Close()
    with _ -> ()

/// Stop the TCP listener server
let stop_tcp reason (server : TcpListener) =
  Log.tracef(fun fmt -> fmt "tcp:stop_tcp - %s - stopping server .. " reason)
  server.Stop()
  Log.trace(fun () -> "tcp:stop_tcp - stopped")

/// Start a new TCP server with a specific IP, Port and with a serve_client worker
/// returning an async workflow whose result can be awaited (for when the tcp server has started
/// listening to its address/port combination), and an asynchronous workflow that
/// yields when the full server is cancelled. If the 'has started listening' workflow
/// returns None, then the start timeout expired.
let tcp_ip_server (source_ip : IPAddress, source_port : uint16) (serve_client : TcpWorker<unit>) =
  let start_data =
    { start_called_utc = DateTime.UtcNow
    ; socket_bound_utc = None
    ; source_ip        = source_ip
    ; source_port      = source_port }
  let accepting_connections = new AsyncResultCell<StartedData>()
  // log "tcp:tcp_ip_server - starting listener: %O" start_data

  let server = new TcpListener(source_ip, int source_port)
  server.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, (int)1)
  server.Start MAX_BACK_LOG

  //consider:
  //echo 5 > /proc/sys/net/ipv4/tcp_fin_timeout
  //echo 1 > /proc/sys/net/ipv4/tcp_tw_recycle
  //custom kernel with shorter TCP_TIMEWAIT_LEN in include/net/tcp.h
  let job (d : #TcpClient) = async {
    use! oo = Async.OnCancel (fun () -> Log.trace(fun () -> "tcp:tcp_ip_server - disconnected client (async cancel)")
                                        close d)
    try
      try
        return! serve_client d
      with 
        | :? System.IO.EndOfStreamException ->
          Log.trace(fun () -> "tcp:tcp_ip_server - disconnected client (end of stream)")
          return ()
        | x ->
          Log.tracef(fun fmt -> fmt "tcp:tcp_ip_server - tcp request processing failed.\n%A" x)
          return ()
    finally close d
  }

  // start a new async worker for each accepted TCP client
  accepting_connections.AwaitResult(), async {
    try
      use! dd = Async.OnCancel(fun () -> stop_tcp "tcp_ip_server async cancelled" server)
      let! (token : Threading.CancellationToken) = Async.CancellationToken

      let start_data = { start_data with socket_bound_utc = Some(DateTime.UtcNow) }
      accepting_connections.Complete start_data |> ignore

      Log.tracef(fun fmt -> fmt "tcp:tcp_ip_server - started listener: %O%s" start_data
                              (if token.IsCancellationRequested then ", cancellation requested" else ""))

      while not (token.IsCancellationRequested) do
        //log "tcp:tcp_ip_server -> async accept tcp client"
        let! client = server.AsyncAcceptTcpClient()
        //log "tcp:tcp_ip_server <- async accept tcp client"
        Async.Start (job client, token)

      return ()
    with x ->
      Log.tracef(fun fmt -> fmt "tcp:tcp_ip_server - tcp server failed.\n%A" x)
      return ()
  }

/// Get the stream from the TCP client
let stream (client : TcpClient) = client.GetStream()

open System.IO

/// Mirror the stream byte-by-byte, one byte at a time
let mirror (client_stream : Stream) (server_stream : Stream) = async {
  try
  while true do
    let! onebyte = client_stream.AsyncRead(1)
    do! server_stream.AsyncWrite onebyte
  with _ -> return ()
}
