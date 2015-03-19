﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Data.Sql;
using System.Data.SqlClient;
using Bitcoin.BitcoinUtilities;
using Bitcoin.Lego.Protocol_Messages;
using Bitcoin.Lego;
using Bitcoin.Lego.Network;
using System.Net;

namespace Bitcoin.Lego.Data_Interface
{
	/// <summary>
	/// A static class designed for interfacing with an SQL Server DB, whether that is in the cloud or local is up to you :)
	/// DEVS PLEASE ENSURE PARAMETERS ARE USED AND NOT JUST APPENDING STRINGS TO BUILD QUERIES AS THIS WILL PROTECT FROM SQL INJECTION ATTACKS THANKS THASHIZNETS
	/// </summary>
	public class DatabaseConnection :IDisposable
	{
		//using LocalDB - can be swapped out for other SQL Server derivatives, including AzureDB. If using Azure, beware the rate limiting, keep an eye for resource exceed exceptions if using Azure DB
		private String _connectionString;
		private SqlConnection _sqlConnectionObj;
		private P2PNetworkParameters _networkParameters;

		public DatabaseConnection(P2PNetworkParameters netParams)
		{
			_networkParameters = netParams;			

			if (_networkParameters.IsTestNet)
			{
				_connectionString = @"Data Source = (localdb)\ProjectsV12; Initial Catalog = Lego.Net TestNet DB; Integrated Security = True; Connect Timeout = 30; Encrypt = False; TrustServerCertificate = False";
			}
			else
			{
				_connectionString= @"Data Source = (localdb)\ProjectsV12;Initial Catalog = Lego.NET DB; Integrated Security = True; Connect Timeout = 30; Encrypt=False;TrustServerCertificate=False";
			}

			_sqlConnectionObj = new SqlConnection(_connectionString);
		}

		public bool CloseDBConnection()
		{
			try
			{
				if (_sqlConnectionObj.State != System.Data.ConnectionState.Closed)
				{
					_sqlConnectionObj.Close();
				}

				return true;
			}
#if (!DEBUG)
			catch
			{

			}
#else
			catch (Exception ex)
			{
                Console.WriteLine("Exception Close Connection: "+ex.Message);
				if (ex.InnerException != null)
				{
					Console.WriteLine("Inner Exception: " + ex.InnerException.Message);
				}
			}
#endif

			return false;
		}

		public bool OpenDBConnection()
		{
			if (!IsOpen)
			{
				try
				{
					//in case the connection is in some weird fucked up state we will attempt to close it before opening it
					_sqlConnectionObj.Close();
				}
#if (!DEBUG)
				catch
				{

				}
#else
				catch (Exception ex)
				{
					Console.WriteLine("Exception Open Connection 1: " + ex.Message);
					if (ex.InnerException != null)
					{
						Console.WriteLine("Inner Exception: " + ex.InnerException.Message);
					}
				}
#endif

				try
				{
					_sqlConnectionObj.Open();
				}
#if (!DEBUG)
				catch
				{
					return false;
				}
#else
				catch (Exception ex)
				{
					Console.WriteLine("Exception Open Connection 2: " + ex.Message);
					if (ex.InnerException != null)
					{
						Console.WriteLine("Inner Exception: " + ex.InnerException.Message);
					}

					return false;
				}
#endif
			}

			return true;
		}

		public bool IsOpen
		{
			get
			{
				if (_sqlConnectionObj.State != System.Data.ConnectionState.Open)
				{
					return false;
				}

				return true;
			}
		}

		public bool AddAddress(PeerAddress addressToAdd)
		{
			try
			{
				if (!IsOpen)
				{
					OpenDBConnection();
				}

				if (addressToAdd.IsRelayExpired)
				{
					return false;
				}

				if (!IsAddressKnown(addressToAdd))
				{
					//note SQL server does not have uint and ulong data types, so we use appropriate size decimal type instead and this preserves the full value
					SqlCommand addAddrCmd = new SqlCommand("INSERT INTO [AddressPool] VALUES (@Param1, @Param2, @Param3, @Param4);", _sqlConnectionObj);
					addAddrCmd.Parameters.Add(new SqlParameter("@Param1", addressToAdd.IPAddress.ToString()));
					addAddrCmd.Parameters.Add(new SqlParameter("@Param2", Convert.ToDecimal(addressToAdd.Time)));
					addAddrCmd.Parameters.Add(new SqlParameter("@Param3", Convert.ToDecimal(addressToAdd.Services)));
					addAddrCmd.Parameters.Add(new SqlParameter("@Param4", Convert.ToDecimal(addressToAdd.Port)));

					if (addAddrCmd.ExecuteNonQuery() < 1)
					{
						return false;
					}
				}
			}
#if (!DEBUG)
			catch
			{
				return false;
			}
#else
			catch (Exception ex)
			{
				Console.WriteLine("Exception Add Address: " + ex.Message);
				if (ex.InnerException != null)
				{
					Console.WriteLine("Inner Exception: " + ex.InnerException.Message);
				}

				return false;
			}
#endif

			//if address is already known to us we say true anyway
			//Pendulum - The Island Pt II (Dusk)
			return true;
		}

		public bool IsAddressKnown(PeerAddress addressToCheck)
		{
			try
			{
				if (!IsOpen)
				{
					OpenDBConnection();
				}

				uint currentTime = addressToCheck.Time;
				int currentPort = addressToCheck.Port;
				ulong currentServices = addressToCheck.Services;

				if (GetAddress(addressToCheck.IPAddress.ToString(), addressToCheck.Port, ref addressToCheck))
				{
					if (currentTime > addressToCheck.Time || currentPort != addressToCheck.Port || currentServices != addressToCheck.Services)
					{
						//newer time update
						UpdateAddressTime(addressToCheck, currentTime);
					}

					return true;
				}
			}
#if (!DEBUG)
			catch
			{

			}
#else
			catch (Exception ex)
			{
				Console.WriteLine("Exception Is Address Known: " + ex.Message);
				if (ex.InnerException != null)
				{
					Console.WriteLine("Inner Exception: " + ex.InnerException.Message);
				}
			}
#endif

			return false;
		}

		public bool UpdateAddressTime(PeerAddress addressToUpdate, uint time)
		{
			try
			{
				if (!IsOpen)
				{
					OpenDBConnection();
				}			

				using (SqlCommand updAddrCmd = new SqlCommand("UPDATE [AddressPool] SET [Time]=@Param1, [Services]=@Param2, [Port]=@Param3 WHERE [IPAddress]=@Param4;", _sqlConnectionObj))
				{
					updAddrCmd.CommandTimeout = 15000;
					updAddrCmd.Parameters.Add(new SqlParameter("@Param1", Convert.ToDecimal(time)));
					updAddrCmd.Parameters.Add(new SqlParameter("@Param2", Convert.ToDecimal(addressToUpdate.Services)));
					updAddrCmd.Parameters.Add(new SqlParameter("@Param3", Convert.ToDecimal(addressToUpdate.Port)));
					updAddrCmd.Parameters.Add(new SqlParameter("@Param4", addressToUpdate.IPAddress.ToString()));
					if (updAddrCmd.ExecuteNonQuery() >= 1)
					{
						return true;
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
				Console.WriteLine("Exception Update Address: " + ex.Message);
				if (ex.InnerException != null)
				{
					Console.WriteLine("Inner Exception: " + ex.InnerException.Message);
				}
			}
#endif

			return false;
		}

		private bool GetAddress(String ip, int port, ref PeerAddress addressToGet)
		{
			try
			{
				if (!IsOpen)
				{
					OpenDBConnection();
				}

				using (SqlCommand getAddrCmd = new SqlCommand("SELECT * FROM [AddressPool] WHERE [IPAddress]=@Param1 AND [Port]=@Param2;", _sqlConnectionObj))
				{
					getAddrCmd.Parameters.Add(new SqlParameter("@Param1", ip));
					getAddrCmd.Parameters.Add(new SqlParameter("@Param2", Convert.ToDecimal(port)));

					using (SqlDataReader dataReader = getAddrCmd.ExecuteReader())
					{

						if (dataReader.Read())
						{
							P2PNetworkParameters useTheseForAddr = new P2PNetworkParameters(P2PNetworkParameters.ProtocolVersion, _networkParameters.IsTestNet, Convert.ToUInt16(dataReader.GetDecimal(3)), Convert.ToUInt64(dataReader.GetDecimal(2)));
                            addressToGet = new PeerAddress(IPAddress.Parse(dataReader.GetString(0)), useTheseForAddr.P2PListeningPort, useTheseForAddr.Services, Convert.ToUInt32(dataReader.GetDecimal(1)), useTheseForAddr);
							dataReader.Close();
							return true;
						}

						dataReader.Close();
					}
				}
			}
			catch (Exception ex)
			{
				if (ex.Message.ToLower().Contains("deadlock"))
				{
					return GetAddress(ip, port, ref addressToGet);
				}
#if(DEBUG)
				Console.WriteLine("Exception Get Address DB: " + ex.Message);
				if (ex.InnerException != null)
				{
					Console.WriteLine("Inner Exception: " + ex.InnerException.Message);
				}
#endif
			}

			return false;
		}

		public List<PeerAddress> GetTopXAddresses(int countx)
		{
			List<PeerAddress> addressesOut = new List<PeerAddress>();

			try
			{
				if (!IsOpen)
				{
					OpenDBConnection();
				}

				using (SqlCommand getAddrCmd = new SqlCommand("SELECT TOP (@Param1) * FROM AddressPool ORDER BY [Time] DESC;", _sqlConnectionObj))
				{
					getAddrCmd.Parameters.Add(new SqlParameter("@Param1", countx));

					using (SqlDataReader dataReader = getAddrCmd.ExecuteReader())
					{

						while(dataReader.Read())
						{
							P2PNetworkParameters netParams = new P2PNetworkParameters(P2PNetworkParameters.ProtocolVersion, _networkParameters.IsTestNet, Convert.ToUInt16(dataReader.GetDecimal(3)), Convert.ToUInt64(dataReader.GetDecimal(2)));
                            addressesOut.Add(new PeerAddress(IPAddress.Parse(dataReader.GetString(0)), netParams.P2PListeningPort, netParams.Services, Convert.ToUInt32(dataReader.GetDecimal(1)),netParams));		
						}
						dataReader.Close();
					}
				}
			}
			catch (Exception ex)
			{
				if (ex.Message.ToLower().Contains("deadlock"))
				{
					return GetTopXAddresses(countx);
				}
#if (DEBUG)
				Console.WriteLine("Exception Get Top X Address DB: " + ex.Message);
				if (ex.InnerException != null)
				{
					Console.WriteLine("Inner Exception: " + ex.InnerException.Message);
				}
#endif
			}

			return addressesOut;
		}

		public String ConnectionString
		{
			get
			{
				return _connectionString;
			}
			set
			{
				_connectionString = value;
			}
		}

		public void Dispose()
		{
			_sqlConnectionObj.Close();
		}
	}
}
