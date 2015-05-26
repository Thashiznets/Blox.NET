﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using Bitcoin.BitcoinUtilities;
using System.Threading;
using Bitcoin.Blox.Data_Interface;
using Open.Nat;

namespace Bitcoin.Blox.Network
{
	public static class P2PConnectionManager
	{
		private static Socket _socket;
		private static IPEndPoint _localEndPoint;
		private static bool _listening = false;
		private static bool _managing = false;
		private static Thread _listenThread;
		private static Thread _manageOutConnectionsThread;
		private static List<P2PConnection> _p2pInboundConnections = new List<P2PConnection>();
		private static List<P2PConnection> _p2pOutboundConnections = new List<P2PConnection>();
		private static long _nodeNetworkOffset = 0;
		public delegate void DelegateAddRemoveP2PConnection(P2PConnection p2pConnection);
		public delegate bool DelegateConnectedToPeer(PeerAddress peer);
		public delegate List<P2PConnection> DelegateListP2PConnections();


		public static async Task<bool> ListenForIncomingP2PConnectionsAsync(IPAddress ipInterfaceToBind, P2PNetworkParameters netParams)
		{
			if (!_listening)
			{
                if (netParams.ListenForPeers)
                {
                    try
                    {
                        LingerOption lo = new LingerOption(false, 0);
                        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        _socket.LingerState = lo;
                        _listening = true;
                        _localEndPoint = new IPEndPoint(ipInterfaceToBind, netParams.P2PListeningPort);
                        if (_socket.IsBound)
                        {
                            _socket.Close();
                        }
                        _socket.Bind(_localEndPoint);
                        _socket.Listen(1000);

                        if (netParams.UPnPMapPort)
                        {
                            //try upnp port forward mapping
                            await SetNATPortForwardingUPnPAsync(netParams.P2PListeningPort, netParams.P2PListeningPort);
                        }

                        _listenThread = new Thread(new ThreadStart(() =>
                        {
                            while (_listening)
                            {
                                try
                                {
                                    Socket newConnectedPeerSock = _socket.Accept();

                                //if we haven't reached maximum specified to connect to us, allow the connection
                                if (GetInboundP2PConnections().Count < P2PNetworkParameters.MaxIncomingP2PConnections)
                                    {
                                    //we've accepted a new peer create a new P2PConnection object to deal with them and we need to be sure to mark it as incoming so it gets stored appropriately
                                    P2PConnection p2pconnecting = new P2PConnection(((IPEndPoint)newConnectedPeerSock.RemoteEndPoint).Address, netParams, newConnectedPeerSock, Convert.ToUInt16(((IPEndPoint)newConnectedPeerSock.RemoteEndPoint).Port), true);
                                        p2pconnecting.ConnectToPeer(0);
                                    }
                                    else
                                    {
                                        newConnectedPeerSock.Close();
                                    }
                                }
                                catch (SocketException sex)
                                {
                                //trap the exception "A blocking operation was interrupted by a call to WSACancelBlockingCall" thrown when we kill the listening socket but throw any others
                                if (sex.ErrorCode != 10004)
                                    {
                                    //he said sex hehehehehehe
                                    throw sex;
                                    }
                                }
                            }
                        }));
                        _listenThread.IsBackground = true;
                        _listenThread.Start();
                    }
#if (!DEBUG)
				    catch
				    {
					    _listening = false;
				    }
#else
                    catch (Exception ex)
                    {
                        _listening = false;

                        Console.WriteLine("Exception Listening For Incoming Connections: " + ex.Message);
                        if (ex.InnerException != null)
                        {
                            Console.WriteLine("Inner Exception: " + ex.InnerException.Message);
                        }
                    }
#endif
                }
                else
                {
#if (DEBUG)
                    Console.WriteLine("Listen For Peers is False so not starting to listen");
#endif

                }
			}

			return _listening;
		}

        public static async Task<bool> SetNATPortForwardingUPnPAsync(int externalPort, int internalPort)
        {
            try
            {
                var nat = new NatDiscoverer();
                var cts = new CancellationTokenSource(5000);
                var device = await nat.DiscoverDeviceAsync(PortMapper.Upnp, cts);

                //purge any old port mapping 
                await device.DeletePortMapAsync(new Mapping(Protocol.Tcp, internalPort, externalPort));

                //now we create the port mapping
                await device.CreatePortMapAsync(new Mapping(Protocol.Tcp, internalPort, externalPort, 0, "Blox.NET Bitcoin Node Port Forward Rule"));

                return true;

            }
#if (!DEBUG)
			catch
			{
				return false;
			}
#else
            catch (Exception ex)
            {
                Console.WriteLine("Exception UPnP Port Forwarding: " + ex.Message);
                if (ex.InnerException != null)
                {
                    Console.WriteLine("Inner Exception: " + ex.InnerException.Message);
                }

                return false;
            }
#endif

        }


        public static bool StopListeningForIncomingP2PConnections()
		{
			try
			{
				_listening = false;
			
				_socket.Close();
            }
#if (!DEBUG)
			catch
			{
				return false;
			}
#else
			catch (Exception ex)
			{
				Console.WriteLine("Exception Stopping Listening For Incoming Connections: " + ex.Message);
				if (ex.InnerException != null)
				{
					Console.WriteLine("Inner Exception: " + ex.InnerException.Message);
				}

				return false;
			}
#endif

			return true;
		}

		public static void MaintainConnectionsOutbound(P2PNetworkParameters netParams)
		{
			//I'm going to preference trying peer relayed addresses if I have some this will assist discovery for new addr addition to the fallback DB, else I'll fall back to dnsseeds from db and elsewhere

			//we havent created the manage thread so create and start it
			if (!_managing)
			{
				_managing = true;

				_manageOutConnectionsThread = new Thread(new ThreadStart(() =>
				{
					Random notCryptoRandom = new Random(Environment.TickCount);

					while (_managing)
					{
                        try
                        {
                            List<P2PConnection> allOutConnections = new List<P2PConnection>(GetOutboundP2PConnections());
                            int attempt = 0;
                            int connectedTo = allOutConnections.Count;

                            //if not connected to maximum outbound peers find and connect to one
                            while (connectedTo < P2PNetworkParameters.MaxOutgoingP2PConnections && attempt < P2PNetworkParameters.MaxOutgoingP2PConnections && _managing)
                            {
                                try
                                {
                                    List<P2PConnection> allConnections = new List<P2PConnection>(GetAllP2PConnections());
                                    P2PConnection connectToMe = null;
                                    PeerAddress pa = null;

                                    //at least 3 peers connected, or we are on testnet so we will start to use peer addresses
                                    if (allConnections.Count >= 3 || (netParams.IsTestNet && allConnections.Count > 0))
                                    {
                                        //get a random peer
                                        P2PConnection p2p = pRandomPeer(allConnections, notCryptoRandom);

                                        //I like to create a new list and fill it so we don't have to worry about the MemAddressPool changing while we are using it
                                        List<PeerAddress> addrs = new List<PeerAddress>(p2p.MemAddressPool);

                                        //at least 10 addresses to try
                                        if (addrs.Count > 10)
                                        {
                                            //get random address from the random peer to try connect
                                            pa = addrs[notCryptoRandom.Next(0, addrs.Count)];
                                            connectToMe = new P2PConnection(pa.IPAddress, netParams, new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp), pa.Port);
                                        }
                                    }

                                    //we dont have a connection so try get one from dns seeds
                                    if (connectToMe == null)
                                    {
                                        //choose a random seed address to connect to
                                        List<PeerAddress> seeds;

                                        if (!netParams.IsTestNet)
                                        {
                                            seeds = GetDNSSeedIPAddresses(P2PNetworkParameters.DNSSeedHosts, netParams);
                                        }
                                        else
                                        {
                                            seeds = GetDNSSeedIPAddresses(P2PNetworkParameters.TestNetDNSSeedHosts, netParams);
                                        }

                                        pa = seeds[notCryptoRandom.Next(0, seeds.Count)];
                                        connectToMe = new P2PConnection(pa.IPAddress, netParams, new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp), pa.Port);
                                    }

                                    if (connectToMe.ConnectToPeer(0, pa.Services))
                                    {
                                        connectedTo++;
                                        //always we sleep 500ms after an outgoing connection
                                        Thread.Sleep(500);
                                    }
                                }
#if (!DEBUG)
							    catch
							    {

    							}
#else
                                catch (Exception ex)
                                {
                                    Console.WriteLine("Exception In Outbound Connection Loop Thread: " + ex.Message);
                                    if (ex.InnerException != null)
                                    {
                                        Console.WriteLine("Inner Exception: " + ex.InnerException.Message);
                                    }
                                }
#endif
                                attempt++;
                            }

                            //we check if we are connected to enough peers every 2 seconds
                            Thread.Sleep(2000);
                        }
#if (!DEBUG)
                        catch
                        {

                        }
#endif
                        catch (Exception ex)
                        {
                            Console.WriteLine("Exception In Outbound Connection Loop Thread Outer Loop: " + ex.Message);
                            if (ex.InnerException != null)
                            {
                                Console.WriteLine("Inner Exception: " + ex.InnerException.Message);
                            }
                        }
                    }
				}));
				_manageOutConnectionsThread.IsBackground = true;
				_manageOutConnectionsThread.Priority = ThreadPriority.Highest;
				_manageOutConnectionsThread.Start();
			}
		}

		private static P2PConnection pRandomPeer(List<P2PConnection> peers, Random notCryptoRandom)
		{
			int attempt = 0;

			P2PConnection peer = peers[notCryptoRandom.Next(0, peers.Count)];

			//8 attempts at finding a peer with addresses in memaddresspool or just give up and return one with no addresses
			while (attempt < 8)
			{
				if (peer.MemAddressPool.Count > 0)
				{
					return peer;
				}

				attempt++;

				peer = peers[notCryptoRandom.Next(0, peers.Count)];
			}

			return peer;			
		}

		public static void AddP2PConnection(P2PConnection p2pConnection)
		{
			if (p2pConnection.InboundConnection)
			{
				lock(_p2pInboundConnections)
				{
					_p2pInboundConnections.Add(p2pConnection);
				}
				return;
			}		

			lock(_p2pOutboundConnections)
			{
				_p2pOutboundConnections.Add(p2pConnection);
			}
		}

		public static void RemoveP2PConnection(P2PConnection p2pConnection)
		{
			try
			{
				if (p2pConnection.InboundConnection)
				{
					lock(_p2pInboundConnections)
					{
						_p2pInboundConnections.RemoveAll(delegate (P2PConnection p2p)
						{
							return p2p.RemoteIPAddress.Equals(p2pConnection.RemoteIPAddress) && p2p.RemotePort.Equals(p2pConnection.RemotePort);
						});
					}

					return;
				}

				lock(_p2pOutboundConnections)
				{
					_p2pOutboundConnections.RemoveAll(delegate (P2PConnection p2p)
					{
						return p2p.RemoteIPAddress.Equals(p2pConnection.RemoteIPAddress) && p2p.RemotePort.Equals(p2pConnection.RemotePort);
					});
				}
			}
#if (!DEBUG)
			catch
			{

			}
#else
			catch (Exception ex)
			{
				Console.WriteLine("Exception Removing P2P Connection: " + ex.Message);
				if (ex.InnerException != null)
				{
					Console.WriteLine("Inner Exception: " + ex.InnerException.Message);
				}
			}
#endif
		}

		public static List<P2PConnection> GetInboundP2PConnections()
		{
			return _p2pInboundConnections;
		}

		public static List<P2PConnection> GetOutboundP2PConnections()
		{
			return _p2pOutboundConnections;
		}

		public static List<P2PConnection> GetAllP2PConnections()
		{
			List<P2PConnection> poolConnections = new List<P2PConnection>();

            lock (_p2pOutboundConnections)
			{
				poolConnections.AddRange(_p2pOutboundConnections);

				lock(_p2pInboundConnections)
				{
					if (_p2pInboundConnections.Count > 0)
					{
						poolConnections.AddRange(_p2pInboundConnections);
					}
				}
			}

			return poolConnections;
		}

		public static bool ConnectedToPeer(PeerAddress peer)
		{
			List<P2PConnection> exists = GetAllP2PConnections().FindAll(delegate (P2PConnection pc)
			{
				return pc.RemoteIPAddress.ToString().Equals(peer.IPAddress.ToString()) && pc.RemotePort.Equals(peer.Port);
            });

			if (exists.Count > 0)
			{
				return true;
			}

			return false;
		}

        public static async Task<List<PeerAddress>> GetDNSSeedIPAddressesAsync(String[] DNSHosts, P2PNetworkParameters netParams)
		{
			List<IPAddress[]> dnsServerIPArrays = new List<IPAddress[]>();
			List<PeerAddress> ipAddressesOut = new List<PeerAddress>();

			//get no more than half the amount of seeds from the known good address DB
			pGetDatabaseIPs(ref ipAddressesOut, netParams);

			//get the rest of the seed nodes from DNS responders
			if (ipAddressesOut.Count < P2PNetworkParameters.SeedNodeCount)
			{
				try
				{

					foreach (String host in DNSHosts)
					{
						try
						{
							IPAddress[] addrs = await Dns.GetHostAddressesAsync(host);
							dnsServerIPArrays.Add(addrs);
						}
						catch
						{
							//allows for continuation if any dns responder goes down
						}
					}

					//we make sure we get addresses from multiple dns responders
					for (int i = 0; i < P2PNetworkParameters.SeedNodeCount || ipAddressesOut.Count >= P2PNetworkParameters.SeedNodeCount; i++)
					{
						foreach (IPAddress[] iparr in dnsServerIPArrays)
						{
							if (ipAddressesOut.Count >= P2PNetworkParameters.SeedNodeCount)
							{
								//we have enough break the loop
								break;
							}
							try
							{
								if (iparr.Length > i)
								{
									PeerAddress pa;

									if (!netParams.IsTestNet)
									{
										pa = new PeerAddress(iparr[i], P2PNetworkParameters.ProdP2PPort, (ulong)P2PNetworkParameters.NODE_NETWORK.FULL_NODE, netParams);
									}
									else
									{
										pa = new PeerAddress(iparr[i], P2PNetworkParameters.TestP2PPort, (ulong)P2PNetworkParameters.NODE_NETWORK.FULL_NODE, netParams);
									}

									if (!ipAddressesOut.Contains(pa))
									{
										ipAddressesOut.Add(pa);
									}
								}
							}
							catch
							{

							}
						}
					}
				}
				catch
				{
					//failed doing dns get so we drop through to hardcoded
				}

				//make sure I always have enough seed nodes else scrounge from hardcoded list *if not testnet
				if (!netParams.IsTestNet)
				{
					pGetFillerIPsFromHardcoded(ref ipAddressesOut, netParams);
				}
			}

			return ipAddressesOut;
		}

		public static List<PeerAddress> GetDNSSeedIPAddresses(String[] DNSHosts, P2PNetworkParameters netParams)
		{
			List<IPAddress[]> dnsServerIPArrays = new List<IPAddress[]>();
			List<PeerAddress> ipAddressesOut = new List<PeerAddress>();

			//get no more than half the amount of seeds from the known good address DB
			pGetDatabaseIPs(ref ipAddressesOut, netParams);

			//get the rest of the seed nodes from DNS responders
			if (ipAddressesOut.Count < P2PNetworkParameters.SeedNodeCount)
			{
				try
				{

					foreach (String host in DNSHosts)
					{
						try
						{
							dnsServerIPArrays.Add(Dns.GetHostAddresses(host));
						}
						catch
						{
							//allows for continuation if any dns server goes down
						}
					}

					for(int i = 0; i < P2PNetworkParameters.SeedNodeCount || ipAddressesOut.Count >= P2PNetworkParameters.SeedNodeCount; i++)
					{
						foreach (IPAddress[] iparr in dnsServerIPArrays)
						{
							if (ipAddressesOut.Count >= P2PNetworkParameters.SeedNodeCount)
							{
								//we have enough break the loop
								break;
							}

							if (iparr.Length > i)
							{
								PeerAddress pa;

								if (!netParams.IsTestNet)
								{
									pa = new PeerAddress(iparr[i], P2PNetworkParameters.ProdP2PPort, (ulong)P2PNetworkParameters.NODE_NETWORK.FULL_NODE, netParams);
								}
								else
								{
									pa = new PeerAddress(iparr[i], P2PNetworkParameters.TestP2PPort, (ulong)P2PNetworkParameters.NODE_NETWORK.FULL_NODE, netParams);
								}

								if (!ipAddressesOut.Contains(pa))
								{
									ipAddressesOut.Add(pa);
								}
							}
						}
					}
				}
				catch
				{
					//failed doing dns get so we drop through to hardcoded
				}

				//make sure I always have enough seed nodes else scrounge from hardcoded list *if not testnet
				if (!netParams.IsTestNet)
				{
					pGetFillerIPsFromHardcoded(ref ipAddressesOut, netParams);
				}
			}

			return ipAddressesOut;
		}

		private static void pGetDatabaseIPs(ref List<PeerAddress> ipAddressesOut, P2PNetworkParameters netParams)
		{
			int diff = (P2PNetworkParameters.SeedNodeCount - ipAddressesOut.Count)/2; //divide this by 2 to ensure a mixture of DB and DNS seeds always

			//get newest addresses from database
			if (diff > 0)
			{
				using (DatabaseConnection dBC = new DatabaseConnection(netParams))
				{
					ipAddressesOut.AddRange(dBC.GetTopXAddresses(diff));
				}
			}
		}

		private static void pGetFillerIPsFromHardcoded(ref List<PeerAddress> ipAddressesOut, P2PNetworkParameters netParams)
		{
			int diff = P2PNetworkParameters.SeedNodeCount - ipAddressesOut.Count;
			Random notCryptoRandom = new Random(DateTime.Now.Millisecond);

			//fallback on randomly selected hardcoded seeds if need be
			if (diff > 0)
			{
				for (int i = 0; i < diff; i++)
				{
					int rIndx = notCryptoRandom.Next(0, HardSeedList.SeedIPStrings.Length);
					PeerAddress pa;

                    if (!netParams.IsTestNet)
					{
						 pa = new PeerAddress(IPAddress.Parse(HardSeedList.SeedIPStrings[rIndx]), P2PNetworkParameters.ProdP2PPort, (ulong)P2PNetworkParameters.NODE_NETWORK.FULL_NODE, netParams);
					}
					else
					{
						pa = new PeerAddress(IPAddress.Parse(HardSeedList.SeedIPStrings[rIndx]), P2PNetworkParameters.TestP2PPort, (ulong)P2PNetworkParameters.NODE_NETWORK.FULL_NODE, netParams);
					}

					ipAddressesOut.Add(pa);
				}
			}
		}

		public static void AddToNodeTimeOffset(long add)
		{
			_nodeNetworkOffset += add;
		}

		public static void SubtractFromNodeTimeOffset(long subtract)
		{
			_nodeNetworkOffset -= subtract;
		}

		public static long NodeTimeOffset
		{
			get
			{
				return _nodeNetworkOffset;
			}
		}

		public static ulong GetUTCNowWithOffset()
		{
			int peerCount = GetAllP2PConnections().Count;
			if (peerCount > 0) //protect from divide by zero
			{
				return Utilities.ToUnixTime(DateTime.UtcNow) + ((ulong)(NodeTimeOffset / GetAllP2PConnections().Count));
			}

			return Utilities.ToUnixTime(DateTime.UtcNow);
        }
	}
}
