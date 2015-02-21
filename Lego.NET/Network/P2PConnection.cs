﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Net;
using System.IO;
using Bitcoin.BitcoinUtilities;
using Bitcoin.Lego.Protocol_Messages;
using Bitcoin.Lego.Data_Interface;

namespace Bitcoin.Lego.Network
{
    public class P2PConnection : Connection
    {
		private VersionMessage _myVersionMessage;
		private VersionMessage _theirVersionMessage;
		private long _peerTimeOffset;
		private bool _inbound;
		private Thread _recieveMessagesThread;
		private Thread _heartbeatThread;
		private Thread _killDeadClientNoHeartbeatThread;
		private Thread _addrFartThread;
		private DateTime _lastRecievedMessage;

		/// <summary>
		/// New P2PConnection Object
		/// </summary>
		/// <param name="remoteIp">IP Address we want to connect to</param>
		/// <param name="connectionTimeout">How many milliseconds to wait for a TCP message</param>
		/// <param name="socket">The socket to use for the data stream, if we don't have one use new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)</param>
		/// <param name="remotePort">The remote port to connect to</param>
		/// <param name="inbound">Is this an inbount P2P connection or are we connecting out</param>
		public P2PConnection(IPAddress remoteIp, int connectionTimeout, Socket socket, int remotePort = Globals.ProdP2PPort, bool inbound = false) :base(remoteIp, remotePort, connectionTimeout, socket)
		{
			_inbound = inbound;
			
			//fully loaded man https://www.youtube.com/watch?v=dLIIKrJ6nnI
		}

		public bool ConnectToPeer(ulong services, uint blockHeight, int relay, bool strictVerAck)
		{
			try
			{
				_addrFartThread = new Thread(new ThreadStart(pSendAddrFart));
				_addrFartThread.IsBackground = true;	

				if (_inbound) //the connection is incoming so we recieve their version message first
				{
					pConnectAndSetVersionAndStreams(services, blockHeight, relay);

					var message = Recieve();

					if (message.GetType().Name.Equals("VersionMessage"))
					{
						_theirVersionMessage = (VersionMessage)message;

						//send my version message
						Send(_myVersionMessage);

						//send verack
						if (pVerifyVersionMessage())
						{
							//it seems accepting the conection is sufficient as no one is sending me veracks on incoming connection so I won't check for verack

							//send addr
							_addrFartThread.Start();

							//start listening for messages
							pMessageListener();
						}
						else
						{
							CloseConnection(true);
						}

					}
					else //something other then their version message...uh oh...not friend... kill the connection
					{
						CloseConnection(true);
					}
				}
				else //the connection is outgoing so we send our version message first
				{
					pConnectAndSetVersionAndStreams(services, blockHeight, relay);

					Send(_myVersionMessage);
					var message = Recieve();

					//we have their version message yay :)
					if (message.GetType().Name.Equals("VersionMessage"))
					{
						_theirVersionMessage = (VersionMessage)message;

						//if strict verack is on we listen for verack and if we don't get it we close the connection
						if (strictVerAck)
						{
							pCheckVerack();
						}

						//not sending verack as seems only a connection recipient acceptee does that
						if (!pVerifyVersionMessage(false))
						{
							CloseConnection(true);
						}
						else
						{
							//send addr will also happen every 24 hrs by default
							_addrFartThread.Start();

							//start listening for messages
							pMessageListener();
						}

					}
					else //something other then their version message...uh oh...not friend... kill the connection
					{
						CloseConnection(true);
					}
				}
			}
#if (!DEBUG)
			catch
			{

			}
#else
			catch (Exception ex)
			{
				Console.WriteLine("Exception: " + ex.Message);
				if (ex.InnerException != null)
				{
					Console.WriteLine("Inner Exception: " + ex.InnerException.Message);
				}
			}
#endif

			return Socket.Connected;
		}

		private void pConnectAndSetVersionAndStreams(ulong services, uint blockHeight, int relay)
		{
			if (!Socket.Connected)
			{
				Socket.Connect(RemoteEndPoint);
			}

			//our in and out streams to the underlying socket
			DataIn = new NetworkStream(Socket, FileAccess.Read);
			DataOut = new NetworkStream(Socket, FileAccess.Write);

			Socket.SendTimeout = Socket.ReceiveTimeout = ConnectionTimeout;

			//add packetMagic for testnet
			_myVersionMessage = new VersionMessage(RemoteIPAddress, Socket, services, RemotePort, blockHeight, relay);

			//set thread for heartbeat
			_heartbeatThread = new Thread(new ThreadStart(pSendHeartbeat));
			_heartbeatThread.IsBackground = true;
			_heartbeatThread.Start();

			//set thread for kill on no heartbeat
			_killDeadClientNoHeartbeatThread = new Thread(new ThreadStart(pNoHeartbeatKillDeadClient));
			_killDeadClientNoHeartbeatThread.IsBackground = true;
			_killDeadClientNoHeartbeatThread.Start();
		}

		private bool pCheckVerack()
		{
			var message = Recieve();

			if (!message.GetType().Name.Equals("VersionAck"))
			{
				CloseConnection(true);
				return false;
			}

			return true;
		}

		private bool pVerifyVersionMessage(bool sendVerack=true)
		{
			//I have their version so time to make sure everything is ok and either verack or reject
			if (_theirVersionMessage != null && Socket.Connected)
			{

				//The client is reporting a version too old for our liking
				if (_theirVersionMessage.ClientVersion < Globals.MinimumAcceptedClientVersion)
				{
					Send(new RejectMessage("version", RejectMessage.ccode.REJECT_OBSOLETE, "Client version needs to be at least " + Globals.MinimumAcceptedClientVersion));
					return false;
				}
				else if (!Utilities.UnixTimeWithin70MinuteThreshold(_theirVersionMessage.Time, out _peerTimeOffset)) //check the unix time timestamp isn't outside 70 minutes, we don't wan't anyone outside 70 minutes anyway....Herpes
				{
					//their time sucks sent a reject message and close connection
					Send(new RejectMessage("version", RejectMessage.ccode.REJECT_INVALID, "Your unix timestamp is fucked up", ""));
					return false;
				}
				else if (_theirVersionMessage.Nonce==_myVersionMessage.Nonce && !Globals.AllowP2PConnectToSelf)
				{
					Send(new RejectMessage("version", RejectMessage.ccode.REJECT_DUPLICATE, "Connecting to self has been disabled", ""));
					return false;
				}
				else //we're good send verack
				{
					if (sendVerack)
					{
						Send(new VersionAck());
					}
					return true;
				}
				
			}

			return false;
		}

		private void pMessageListener()
		{
			_recieveMessagesThread = new Thread(new ThreadStart(() =>
			{
				while (Socket.Connected)
				{
					try
					{
						var message = Recieve();

						//process the message appropriately
						switch (message.GetType().Name)
						{
							case "Ping":
								//send pong responce to ping
								Send(new Pong(((Ping)message).Nonce));
								break;

							case "Pong":
								//we have pong
								break;

							case "RejectMessage":
#if(DEBUG)						//if we run in debug I spew out to console, in production no to save from an attack that slows us down writing to the console
								Console.WriteLine(((RejectMessage)message).Message + " - " + ((RejectMessage)message).CCode + " - " + ((RejectMessage)message).Reason + " - " + ((RejectMessage)message).Data);
#endif
								break;

							case "AddressMessage":
								Thread _recieveAddressesThread = new Thread(new ThreadStart(() =>
								{
									DatabaseConnection dbC = new DatabaseConnection();
									dbC.OpenDBConnection();

									foreach (PeerAddress pa in ((AddressMessage)message).Addresses)
									{										
										dbC.AddAddress(pa);
									}

									dbC.CloseDBConnection();
								}));
								_recieveAddressesThread.IsBackground = true;
								_recieveAddressesThread.Start();
								break;

							case "GetAddresses":
								//to do spawn a new thread and handle dishing out addresses
								PeerAddress _my_net_addr = Connection.GetMyExternalIP(_myVersionMessage.LocalServices);
								Send(new AddressMessage(new List<PeerAddress>() { _my_net_addr }));
								break;

							case "NullMessage":
								try
								{
									this.CloseConnection(true);
								}
								catch
								{

								}
								return;
								
							default: //if it's something we don't know about we just ignore it
								break;
						}
					}
#if (!DEBUG)
					catch
					{

					}
#else
					catch (Exception ex)
					{
						Console.WriteLine("Exception: " + ex.Message);
						if (ex.InnerException != null)
						{
							Console.WriteLine("Inner Exception: " + ex.InnerException.Message);
						}
					}
#endif
				}
			}));
			_recieveMessagesThread.IsBackground = true;
			_recieveMessagesThread.Start();
		}

		public void CloseConnection(bool forget=false)
		{
			try
			{
				_heartbeatThread.Abort();
			}
			catch
			{

			}

			try
			{
				_killDeadClientNoHeartbeatThread.Abort();
			}
			catch
			{

			}

			if (Socket.Connected)
			{
				Socket.Close();
			}

			if (forget)
			{
				P2PListener.RemoveP2PConnection(this);
			}
		}

		private void pSendAddrFart()
		{
			while (Socket.Connected)
			{
				try
				{		
					PeerAddress _my_net_addr = Connection.GetMyExternalIP(_myVersionMessage.LocalServices);
					Send(new AddressMessage(new List<PeerAddress>() {_my_net_addr }));
					Thread randomGetAddrTimeThread = new Thread(new ThreadStart(pRandomTimeSendGetAddr));
					randomGetAddrTimeThread.IsBackground = true;
					randomGetAddrTimeThread.Start();
					Thread.CurrentThread.Join(Globals.AddrFartInterval);
				}
#if (!DEBUG)
				catch
				{

				}
#else
				catch (Exception ex)
				{
					Console.WriteLine("Exception: " + ex.Message);
					if (ex.InnerException != null)
					{
						Console.WriteLine("Inner Exception: " + ex.InnerException.Message);
					}
				}
#endif
			}
		}

		private void pRandomTimeSendGetAddr()
		{
			int sleep = new Random(DateTime.Now.Millisecond).Next(1, 20);
			Thread.CurrentThread.Join(sleep * 30000);
			Send(new GetAddresses());
		}

		private void pSendHeartbeat()
		{
			while (Socket.Connected)
			{
				try
				{
					Thread.CurrentThread.Join(Globals.HeartbeatTimeout); //send a heartbeat after the specified interval

					if (Globals.HeartbeatKeepAlive)
					{
						Send(new Ping());
					}
				}
#if (!DEBUG)
				catch
				{

				}
#else
				catch (Exception ex)
				{
					Console.WriteLine("Exception: " + ex.Message);
					if (ex.InnerException != null)
					{
						Console.WriteLine("Inner Exception: " + ex.InnerException.Message);
					}
				}
#endif
			}
		}

		private void pNoHeartbeatKillDeadClient()
		{
			int timeWait = (Globals.HeartbeatTimeout * 3); //90 minutes for kill (3 times specified heartbeat signal) so I just multiply heartbeat by 3

            while (Socket.Connected)
			{
				try
				{
					Thread.CurrentThread.Join(timeWait);

					if (Globals.DeadIfNoHeartbeat)
					{
						TimeSpan timeLapsed = DateTime.UtcNow - _lastRecievedMessage;

						TimeSpan timeOut = new TimeSpan(timeWait*10000);

						if (timeLapsed > timeOut)
						{
							//no heartbeat time to kill connection
							CloseConnection(true);
						}

						timeWait = (Globals.HeartbeatTimeout*3) - Convert.ToInt32(timeLapsed.TotalMilliseconds);
					}
				}
#if (!DEBUG)
				catch
				{

				}
#else
				catch (Exception ex)
				{
					Console.WriteLine("Exception: " + ex.Message);
					if (ex.InnerException != null)
					{
						Console.WriteLine("Inner Exception: " + ex.InnerException.Message);
					}
				}
#endif
			}
		}

		public static async Task<List<IPAddress>> GetDNSSeedIPAddressesAsync(String[] DNSHosts)
		{
			List<IPAddress[]> dnsServerIPArrays = new List<IPAddress[]>();
			List<IPAddress> ipAddressesOut = new List<IPAddress>();

			foreach (String host in DNSHosts)
			{
				IPAddress[] addrs = await Dns.GetHostAddressesAsync(host);
				dnsServerIPArrays.Add(addrs);
			}

			foreach (IPAddress[] iparr in dnsServerIPArrays)
			{
				foreach (IPAddress ip in iparr)
				{
					if (!ipAddressesOut.Contains(ip))
					{
						ipAddressesOut.Add(ip);
					}
				}
			}

			//make sure I always have at least 100 seed nodes to check against
			pGetHardcodedFillerIPs(ref ipAddressesOut);

			return ipAddressesOut;
		}

		private static void pGetHardcodedFillerIPs(ref List<IPAddress> ipAddressesOut)
		{
			Random notCryptoRandom = new Random(DateTime.Now.Millisecond);

			for (int i = 0; i < (100 - ipAddressesOut.Count); i++)
			{
				int rIndx = notCryptoRandom.Next(0, (HardSeedList.SeedIPStrings.Length - 1));
				ipAddressesOut.Add(IPAddress.Parse(HardSeedList.SeedIPStrings[rIndx]));
			}
		}

		public static List<IPAddress> GetDNSSeedIPAddresses(String[] DNSHosts)
		{
			List<IPAddress[]> dnsServerIPArrays = new List<IPAddress[]>();
			List<IPAddress> ipAddressesOut = new List<IPAddress>();

			foreach (String host in DNSHosts)
			{

				dnsServerIPArrays.Add(Dns.GetHostAddresses(host));
			}

			foreach (IPAddress[] iparr in dnsServerIPArrays)
			{
				foreach (IPAddress ip in iparr)
				{
					if (!ipAddressesOut.Contains(ip))
					{
						ipAddressesOut.Add(ip);
					}
				}
			}

			//make sure I always have at least 200 seed nodes to check against
			pGetHardcodedFillerIPs(ref ipAddressesOut);

			return ipAddressesOut;
		}


		/// <summary>
		/// Broadcasts a message to all connected P2P peers
		/// </summary>
		/// <param name="message">Message to broadcast</param>
		/// <param name="exclusions">P2PConnections you don't want to recieve the message for example if its a relayed message we don't want to return to sender</param>
		public static void BradcastSend(Message message, List<P2PConnection> exclusions)
		{
			Thread broadcastMessageThread = new Thread(new ThreadStart(() =>
			{
				foreach (P2PConnection p2p in P2PListener.GetP2PConnections())
				{
					if (!exclusions.Contains(p2p))
					{
						p2p.Send(message);
					}
				}
			}));
			broadcastMessageThread.IsBackground = true;
			broadcastMessageThread.Start();
		}

		/// <summary>
		///  Broadcasts a message to all connected P2P peers
		/// </summary>
		/// <param name="message">Message to broadcast</param>
		public static void BradcastSend(Message message)
		{
			Thread broadcastMessageThread = new Thread(new ThreadStart(() =>
			{
				foreach (P2PConnection p2p in P2PListener.GetP2PConnections())
				{
					p2p.Send(message);
				}
			}));
			broadcastMessageThread.IsBackground = true;
			broadcastMessageThread.Start();
		}

		public bool Send(Message message)
		{
			if (Socket.Connected)
			{
				int attempt = 1;

				while (attempt <= Globals.RetrySendTCPOnError)
				{
					try
					{
						WriteMessage(message);
						return true;
					}
#if (!DEBUG)
					catch
					{

					}
#else
					catch (Exception ex)
					{
						Console.WriteLine("Exception: " + ex.Message);
						if (ex.InnerException != null)
						{
							Console.WriteLine("Inner Exception: " + ex.InnerException.Message);
						}
					}
#endif
					attempt++;
				}
			}
			else
			{
				this.CloseConnection(true);
			}

			return false;
		}

		public Message Recieve()
		{
			if (Socket.Connected)
			{
				int attempt = 1;

				while (attempt <= Globals.RetryRecieveTCPOnError)
				{
					try
					{
						var msg = ReadMessage();
						_lastRecievedMessage = DateTime.UtcNow;
						return msg;
					}
#if (!DEBUG)
					catch
					{

					}
#else
					catch (Exception ex)
					{
						Console.WriteLine("Recieve Message Exception Attempt " + attempt + ": " + ex.Message);
						if (ex.InnerException != null)
						{
							Console.WriteLine("Inner Exception: " + ex.InnerException.Message);
						}
					}
#endif
					attempt++;
				}
			}
			
			return new NullMessage();
		}

		public VersionMessage MyVersionMessage
		{
			get
			{
				return _myVersionMessage;
			}
		}

		public VersionMessage TheirVersionMessage
		{
			get
			{
				return _theirVersionMessage;
			}
		}
		
		public long PeerTimeOffset
		{
			get
			{
				return _peerTimeOffset;
			}
		}
		
		public bool Connected
		{
			get
			{
				return Socket.Connected;
			}
		}

		public bool InboundConnection
		{
			get
			{
				return _inbound;
			}
		}
	}
}
