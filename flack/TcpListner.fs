﻿namespace flack
    open System
    open System.Net
    open System.Net.Sockets
    open System.Collections.Generic
    open System.Collections.Concurrent
    open SocketExtensions
    open utility
    
    open System.Reflection
    [<assembly: AssemblyVersion("0.1.0.*")>] 
    do()
            
    type TcpListener(maxaccepts, maxsends, maxreceives, size, port, backlog, sent, received, connected, disconnected) as this =

        let createTcpSocket() =
            new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)

        let createListener (ip:IPAddress, port, backlog) =
            let s = createTcpSocket()
            s.Bind(new IPEndPoint(ip, port))
            s.Listen(backlog)
            s

        let listeningSocket = createListener( IPAddress.Loopback, port, backlog)

        let initPool (maxinpool, callback) =
            let pool = new BlockingCollection<SocketAsyncEventArgs>(maxinpool:int)
            let rec loop n =
                match n with
                | x when x < maxinpool -> 
                    let saea = new SocketAsyncEventArgs()
                    saea.Completed |> Observable.add callback 
                    pool.Add saea
                    loop (n+1)
                | _ -> ()
            loop 0
            pool

        let acceptPool = initPool (maxaccepts, this.acceptcompleted)

        let mutable disposed = false
        
        let cleanUp() = 
            if not disposed then
                disposed <- true
                listeningSocket.Shutdown(SocketShutdown.Both)
                listeningSocket.Disconnect(false)
                listeningSocket.Close()

        let connectClient acceptSocket = new Connection (maxreceives, maxsends, size, acceptSocket, sent, received, disconnected)

        member this.Clients = new System.Collections.Concurrent.ConcurrentDictionary<IPEndPoint, Connection>(4, 1000)

        member this.acceptcompleted (args : SocketAsyncEventArgs) =
            try
                match args.LastOperation with
                | SocketAsyncOperation.Accept ->
                    match args.SocketError with
                    | SocketError.Success -> 
                        listeningSocket.AcceptAsyncSafe( this.acceptcompleted, acceptPool.Take())
                        let connection = connectClient args.AcceptSocket (*create new connection passing in the socket*)
                        let acceptSocket = args.AcceptSocket.RemoteEndPoint :?> IPEndPoint (*grab remote endpoint*)
                        acceptSocket |?> connected 
                        let success = this.Clients.TryAdd(acceptSocket, connection) (*add client to dictionary*)
                        if not success then 
                            failwith "client could not be added"
                        else
                        connection.Start() (*start the new connection*)
                        args.AcceptSocket <- null (*remove the AcceptSocket because we will be reusing args*)
                    | _ -> 
                        // ** Fix: this will never fire we have to hook up the connection objects disconnect socket down through here
                        //args.AcceptSocket.RemoteEndPoint :?> IPEndPoint |?> disconnected
                        args.SocketError.ToString() |> printfn "socket error on accept: %s"  
                | _ -> args.LastOperation |> failwith "Unknown operation, should be accept but was %a"            
            finally
                acceptPool.Add(args)

        member this.Send(client, msg:byte[]) =
            let success, client = this.Clients.TryGetValue(client)
            match success with
            | true -> client.Send(msg)
            | _ ->  failwith "could not find client %"

        member this.start () =   
            listeningSocket.AcceptAsyncSafe( this.acceptcompleted, acceptPool.Take())

        member this.Close() =
            cleanUp()

        interface IDisposable with
            member this.Dispose() = cleanUp()

        new(port, sent, received, connected, disconnected) = new TcpListener(10,10,10, 1024, port, 100, sent, received, connected, disconnected)