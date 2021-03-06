﻿using System;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using MantaMTA.Core.DAL;
using MantaMTA.Core.Enums;
using WebInterfaceLib.BO;

namespace WebInterfaceLib.DAL
{
	public static class TransactionDB
	{
		/// <summary>
		/// Gets information about the speed of a send.
		/// </summary>
		/// <param name="sendID">ID of the send to get speed information about.</param>
		/// <returns>SendSpeedInfo</returns>
		public static SendSpeedInfo GetSendSpeedInfo(string sendID)
		{
			using (SqlConnection conn = MantaDB.GetSqlConnection())
			{
				SqlCommand cmd = conn.CreateCommand();
				cmd.CommandText = @"
DECLARE @internalSendID int
SELECT @internalSendID = mta_send_internalId
FROM man_mta_send
WHERE mta_send_id = @sndID

SELECT COUNT(*) AS 'Count', [tran].mta_transactionStatus_id, CONVERT(smalldatetime, [tran].mta_transaction_timestamp) as 'mta_transaction_timestamp'
FROM man_mta_transaction as [tran] with(nolock)
JOIN man_mta_msg AS [msg] with(nolock) ON [tran].mta_msg_id = [msg].mta_msg_id
WHERE [msg].mta_send_internalId = @internalSendID
GROUP BY [tran].mta_transactionStatus_id, CONVERT(smalldatetime, [tran].mta_transaction_timestamp)
ORDER BY CONVERT(smalldatetime, [tran].mta_transaction_timestamp)";
				cmd.Parameters.AddWithValue("@sndID", sendID);
				return new SendSpeedInfo(DataRetrieval.GetCollectionFromDatabase<SendSpeedInfoItem>(cmd, CreateAndFillSendSpeedInfoItemFromRecord));
			}
		}

		/// <summary>
		/// Gets information about the speed of sending over the last one hour.
		/// </summary>
		/// <returns>SendSpeedInfo</returns>
		public static SendSpeedInfo GetLastHourSendSpeedInfo()
		{
			using (SqlConnection conn = MantaDB.GetSqlConnection())
			{
				SqlCommand cmd = conn.CreateCommand();
				cmd.CommandText = @"
SELECT COUNT(*) AS 'Count', [tran].mta_transactionStatus_id, CONVERT(smalldatetime, [tran].mta_transaction_timestamp) as 'mta_transaction_timestamp'
FROM man_mta_transaction as [tran] WITH (nolock)
WHERE [tran].mta_transaction_timestamp >= DATEADD(HOUR, -1, GETUTCDATE())
GROUP BY [tran].mta_transactionStatus_id, CONVERT(smalldatetime, [tran].mta_transaction_timestamp)
ORDER BY CONVERT(smalldatetime, [tran].mta_transaction_timestamp)";
				return new SendSpeedInfo(DataRetrieval.GetCollectionFromDatabase<SendSpeedInfoItem>(cmd, CreateAndFillSendSpeedInfoItemFromRecord));
			}
		}

		/// <summary>
		/// Creates a SendSpeedInfoItem object and fills it with data from the data record.
		/// </summary>
		/// <param name="record">Contains the data to use for filling.</param>
		/// <returns>A SendSpeedInfoItem object filled with data from the record.</returns>
		private static SendSpeedInfoItem CreateAndFillSendSpeedInfoItemFromRecord(IDataRecord record)
		{
			SendSpeedInfoItem item = new SendSpeedInfoItem
			{
				Count = record.GetInt64("Count"),
				Status = (TransactionStatus)record.GetInt64("mta_transactionStatus_id"),
				Timestamp = record.GetDateTime("mta_transaction_timestamp")
			};
			return item;
		}

		/// <summary>
		/// Gets a data page about bounces from the transactions table for a send.
		/// </summary>
		/// <param name="sendID">Send to get data for.</param>
		/// <param name="pageNum">The page to get.</param>
		/// <param name="pageSize">The size of the data pages.</param>
		/// <returns>An array of BounceInfo from the data page.</returns>
		public static BounceInfo[] GetBounceInfo(string sendID, int pageNum, int pageSize)
		{
			bool hasSendID = !string.IsNullOrWhiteSpace(sendID);
			using (SqlConnection conn = MantaDB.GetSqlConnection())
			{
				SqlCommand cmd = conn.CreateCommand();
				cmd.CommandText = (hasSendID ? @"
declare @internalSendID int
SELECT @internalSendID = mta_send_internalId
FROM man_mta_send WITH(nolock)
WHERE mta_send_id = @sndID
" : string.Empty) + @"
SELECT [sorted].*
FROM (
		SELECT ROW_NUMBER() OVER (ORDER BY count(*) DESC, mta_transaction_serverHostname) as 'Row',
			   mta_transactionStatus_id, 
			   mta_transaction_serverResponse, 
			   mta_transaction_serverHostname as 'mta_transaction_serverHostname', 
			   [ip].ip_ipAddress_hostname, 
			   [ip].ip_ipAddress_ipAddress, COUNT(*) as 'Count',
			   MAX(mta_transaction_timestamp) as 'LastOccurred'
		FROM man_mta_transaction as [tran] with(nolock)
		JOIN man_mta_msg as [msg] with(nolock) ON [tran].mta_msg_id = [msg].mta_msg_id
		JOIN man_ip_ipAddress as [ip] ON [tran].ip_ipAddress_id = [ip].ip_ipAddress_id
		WHERE mta_transactionStatus_id IN (1, 2, 3, 6) --// Todo: Make this enum!
		" + (hasSendID ? "AND [msg].mta_send_internalId = @internalSendID " : string.Empty) + @"
		GROUP BY mta_transactionStatus_id, mta_transaction_serverResponse, mta_transaction_serverHostname,[ip].ip_ipAddress_hostname, [ip].ip_ipAddress_ipAddress
	 ) as [sorted]
WHERE [Row] >= " + (((pageNum * pageSize) - pageSize) + 1) + " AND [Row] <= " + (pageNum * pageSize);
				if (hasSendID)
					cmd.Parameters.AddWithValue("@sndID", sendID);
				return DataRetrieval.GetCollectionFromDatabase<BounceInfo>(cmd, CreateAndFillBounceInfo).ToArray();
			}
		}

		/// <summary>
		/// Gets a data page about bounces from the transactions table for a send.
		/// </summary>
		/// <param name="sendID">Send to get data for.</param>
		/// <param name="pageNum">The page to get.</param>
		/// <param name="pageSize">The size of the data pages.</param>
		/// <returns>An array of BounceInfo from the data page.</returns>
		public static BounceInfo[] GetFailedInfo(string sendID, int pageNum, int pageSize)
		{
			bool hasSendID = !string.IsNullOrWhiteSpace(sendID);
			using (SqlConnection conn = MantaDB.GetSqlConnection())
			{
				SqlCommand cmd = conn.CreateCommand();
				cmd.CommandText = (hasSendID ? @"
declare @internalSendID int
SELECT @internalSendID = mta_send_internalId
FROM man_mta_send WITH(nolock)
WHERE mta_send_id = @sndID
" : string.Empty) + @"
SELECT [sorted].*
FROM (
		SELECT ROW_NUMBER() OVER (ORDER BY COUNT(*) DESC, mta_transaction_serverHostname) as 'Row', 
			   mta_transactionStatus_id, 
			   mta_transaction_serverResponse, 
			   mta_transaction_serverHostname as 'mta_transaction_serverHostname', 
			   [ip].ip_ipAddress_hostname, 
			   [ip].ip_ipAddress_ipAddress, COUNT(*) as 'Count',
			   MAX(mta_transaction_timestamp) as 'LastOccurred'
		FROM man_mta_transaction as [tran] WITH(nolock)
		JOIN man_mta_msg as [msg] WITH(nolock) ON [tran].mta_msg_id = [msg].mta_msg_id
		JOIN man_ip_ipAddress as [ip] ON [tran].ip_ipAddress_id = [ip].ip_ipAddress_id
		WHERE mta_transactionStatus_id IN (2, 3, 6) --// Todo: Make this enum!
		" + (hasSendID ? "AND [msg].mta_send_internalId = @internalSendID " : string.Empty) + @"
		GROUP BY mta_transactionStatus_id, mta_transaction_serverResponse, mta_transaction_serverHostname,[ip].ip_ipAddress_hostname, [ip].ip_ipAddress_ipAddress
	 ) as [sorted]
WHERE [Row] >= " + (((pageNum * pageSize) - pageSize) + 1) + " AND [Row] <= " + (pageNum * pageSize);
				if (hasSendID)
					cmd.Parameters.AddWithValue("@sndID", sendID);
				return DataRetrieval.GetCollectionFromDatabase<BounceInfo>(cmd, CreateAndFillBounceInfo).ToArray();
			}
		}

		/// <summary>
		/// Gets a data page about bounces from the transactions table for a send.
		/// </summary>
		/// <param name="sendID">Send to get data for.</param>
		/// <param name="pageNum">The page to get.</param>
		/// <param name="pageSize">The size of the data pages.</param>
		/// <returns>An array of BounceInfo from the data page.</returns>
		public static BounceInfo[] GetDeferralInfo(string sendID, int pageNum, int pageSize)
		{
			bool hasSendID = !string.IsNullOrWhiteSpace(sendID);
			using (SqlConnection conn = MantaDB.GetSqlConnection())
			{
				SqlCommand cmd = conn.CreateCommand();
				cmd.CommandText = (hasSendID ? @"
declare @internalSendID int
SELECT @internalSendID = mta_send_internalId
FROM man_mta_send
WHERE mta_send_id = @sndID
" : string.Empty) + @"
SELECT [sorted].*
FROM (
		SELECT ROW_NUMBER() OVER (ORDER BY COUNT(*) DESC, mta_transaction_serverHostname) as 'Row', 
			   mta_transactionStatus_id, 
			   mta_transaction_serverResponse, 
			   mta_transaction_serverHostname as 'mta_transaction_serverHostname', 
			   [ip].ip_ipAddress_hostname, 
			   [ip].ip_ipAddress_ipAddress, COUNT(*) as 'Count',
			   MAX(mta_transaction_timestamp) as 'LastOccurred'
		FROM man_mta_transaction as [tran] WITH(nolock)
		JOIN man_mta_msg as [msg] WITH(nolock) ON [tran].mta_msg_id = [msg].mta_msg_id
		JOIN man_ip_ipAddress as [ip] ON [tran].ip_ipAddress_id = [ip].ip_ipAddress_id
		WHERE mta_transactionStatus_id IN (1) --// Todo: Make this enum!
		" + (hasSendID ? "AND [msg].mta_send_internalId = @internalSendID " : string.Empty) + @"
		GROUP BY mta_transactionStatus_id, mta_transaction_serverResponse, mta_transaction_serverHostname,[ip].ip_ipAddress_hostname, [ip].ip_ipAddress_ipAddress
	 ) as [sorted]
WHERE [Row] >= " + (((pageNum * pageSize) - pageSize) + 1) + " AND [Row] <= " + (pageNum * pageSize);
				if (hasSendID)
					cmd.Parameters.AddWithValue("@sndID", sendID);
				return DataRetrieval.GetCollectionFromDatabase<BounceInfo>(cmd, CreateAndFillBounceInfo).ToArray();
			}
		}

		/// <summary>
		/// Gets the most common bounces from the last hour.
		/// </summary>
		/// <param name="count">Amount of bounces to get.</param>
		/// <returns>Information about the bounces</returns>
		public static BounceInfo[] GetLastHourBounceInfo(int count)
		{
			using (SqlConnection conn = MantaDB.GetSqlConnection())
			{
				SqlCommand cmd = conn.CreateCommand();
				cmd.CommandText = @"
SELECT TOP " + count + @" ROW_NUMBER() OVER (ORDER BY COUNT(*) DESC, mta_transaction_serverHostname) as 'Row', 
			   mta_transactionStatus_id, 
			   mta_transaction_serverResponse, 
			   mta_transaction_serverHostname as 'mta_transaction_serverHostname', 
			   [ip].ip_ipAddress_hostname, 
			   [ip].ip_ipAddress_ipAddress, COUNT(*) as 'Count',
			   MAX(mta_transaction_timestamp) as 'LastOccurred'
FROM man_mta_transaction as [tran] WITH (nolock)
JOIN man_mta_msg as [msg] WITH(nolock) ON [tran].mta_msg_id = [msg].mta_msg_id
JOIN man_ip_ipAddress as [ip] ON [tran].ip_ipAddress_id = [ip].ip_ipAddress_id
WHERE [tran].mta_transaction_timestamp >= DATEADD(HOUR, -1, GETUTCDATE()) 
AND mta_transactionStatus_id IN (1, 2, 3, 6)
AND mta_transaction_serverHostname NOT LIKE ''
GROUP BY mta_transactionStatus_id, mta_transaction_serverResponse, mta_transaction_serverHostname,[ip].ip_ipAddress_hostname, [ip].ip_ipAddress_ipAddress
ORDER BY COUNT(*) DESC";
				return DataRetrieval.GetCollectionFromDatabase<BounceInfo>(cmd, CreateAndFillBounceInfo).ToArray();
			}
		}

		/// <summary>
		/// Counts the total amount of bounces for a send.
		/// </summary>
		/// <param name="sendID">ID of the send to count bounces for.</param>
		/// <returns>The amount of bounces for the send.</returns>
		public static int GetBounceCount(string sendID)
		{
			bool hasSendID = !string.IsNullOrWhiteSpace(sendID);
			using (SqlConnection conn = MantaDB.GetSqlConnection())
			{
				SqlCommand cmd = conn.CreateCommand();
				cmd.CommandText = (hasSendID ? @"
declare @internalSendID int
SELECT @internalSendID = mta_send_internalId
FROM man_mta_send
WHERE mta_send_id = @sndID
" : string.Empty) + @"
SELECT COUNT(*)
FROM(
SELECT 1 as 'Col'
		FROM man_mta_transaction as [tran]
		JOIN man_mta_msg as [msg] ON [tran].mta_msg_id = [msg].mta_msg_id
		JOIN man_ip_ipAddress as [ip] ON [tran].ip_ipAddress_id = [ip].ip_ipAddress_id
		WHERE mta_transactionStatus_id IN (1, 2, 3, 6) --// Todo: Make this enum! 
		" + (hasSendID ? "AND [msg].mta_send_internalId = @internalSendID" : string.Empty) + @"
	GROUP BY mta_transactionStatus_id, mta_transaction_serverResponse, mta_transaction_serverHostname,[ip].ip_ipAddress_hostname, [ip].ip_ipAddress_ipAddress
	) as [sorted]";
				if(hasSendID)
					cmd.Parameters.AddWithValue("@sndID", sendID);
				conn.Open();
				return Convert.ToInt32(cmd.ExecuteScalar());
			}
		}

		/// <summary>
		/// Counts the total amount of deferrals for a send.
		/// </summary>
		/// <param name="sendID">ID of the send to count bounces for.</param>
		/// <returns>The amount of deferrals for the send.</returns>
		public static int GetDeferredCount(string sendID)
		{
			bool hasSendID = !string.IsNullOrWhiteSpace(sendID);
			using (SqlConnection conn = MantaDB.GetSqlConnection())
			{
				SqlCommand cmd = conn.CreateCommand();
				cmd.CommandText = (hasSendID ? @"
declare @internalSendID int
SELECT @internalSendID = mta_send_internalId
FROM man_mta_send WITH(nolock)
WHERE mta_send_id = @sndID
" : string.Empty) + @"
SELECT COUNT(*)
FROM(
SELECT 1 as 'Col'
		FROM man_mta_transaction as [tran] WITH(nolock)
		JOIN man_mta_msg as [msg] WITH(nolock) ON [tran].mta_msg_id = [msg].mta_msg_id
		JOIN man_ip_ipAddress as [ip] ON [tran].ip_ipAddress_id = [ip].ip_ipAddress_id
		WHERE mta_transactionStatus_id IN (1) --// Todo: Make this enum! 
		" + (hasSendID ? "AND [msg].mta_send_internalId = @internalSendID" : string.Empty) + @"
	GROUP BY mta_transactionStatus_id, mta_transaction_serverResponse, mta_transaction_serverHostname,[ip].ip_ipAddress_hostname, [ip].ip_ipAddress_ipAddress
	) as [sorted]";
				if (hasSendID)
					cmd.Parameters.AddWithValue("@sndID", sendID);
				conn.Open();
				return Convert.ToInt32(cmd.ExecuteScalar());
			}
		}

		/// <summary>
		/// Counts the total amount of deferrals for a send.
		/// </summary>
		/// <param name="sendID">ID of the send to count bounces for.</param>
		/// <returns>The amount of deferrals for the send.</returns>
		public static int GetFailedCount(string sendID)
		{
			bool hasSendID = !string.IsNullOrWhiteSpace(sendID);
			using (SqlConnection conn = MantaDB.GetSqlConnection())
			{
				SqlCommand cmd = conn.CreateCommand();
				cmd.CommandText = (hasSendID ? @"
declare @internalSendID int
SELECT @internalSendID = mta_send_internalId
FROM man_mta_send WITH(nolock)
WHERE mta_send_id = @sndID
" : string.Empty) + @"
SELECT COUNT(*)
FROM(
SELECT 1 as 'Col'
		FROM man_mta_transaction as [tran] WITH(nolock)
		JOIN man_mta_msg as [msg] WITH(nolock) ON [tran].mta_msg_id = [msg].mta_msg_id
		JOIN man_ip_ipAddress as [ip] ON [tran].ip_ipAddress_id = [ip].ip_ipAddress_id
		WHERE mta_transactionStatus_id IN (2, 3, 6) --// Todo: Make this enum! 
		" + (hasSendID ? "AND [msg].mta_send_internalId = @internalSendID" : string.Empty) + @"
	GROUP BY mta_transactionStatus_id, mta_transaction_serverResponse, mta_transaction_serverHostname,[ip].ip_ipAddress_hostname, [ip].ip_ipAddress_ipAddress
	) as [sorted]";
				if (hasSendID)
					cmd.Parameters.AddWithValue("@sndID", sendID);
				conn.Open();
				return Convert.ToInt32(cmd.ExecuteScalar());
			}
		}

		/// <summary>
		/// Gets the total deferred and rejected counts.
		/// </summary>
		/// <param name="deferred">Returns the deferred count.</param>
		/// <param name="rejected">Returns the rejected count.</param>
		public static void GetBounceDeferredAndRejected(out long deferred, out long rejected)
		{
			using (SqlConnection conn = MantaDB.GetSqlConnection())
			{
				SqlCommand cmd = conn.CreateCommand();
				cmd.CommandText = @"
declare @deferred bigint
declare @rejected bigint

SELECT @deferred = COUNT(*)
FROM man_mta_transaction WITH(nolock)
WHERE mta_transactionStatus_id = 1

SELECT @rejected = COUNT(*)
FROM man_mta_transaction WITH(nolock)
WHERE mta_transactionStatus_id IN (2, 3, 6)

SELECT @deferred as 'Deferred', @rejected as 'Rejected'";			
				conn.Open();
				SqlDataReader reader = cmd.ExecuteReader();
				reader.Read();
				deferred = Convert.ToInt64(reader["Deferred"]);
				rejected = Convert.ToInt64(reader["Rejected"]);
				reader.Close();
			}
		}

		/// <summary>
		/// Creates a BounceInfo object and fills it with data from the data record.
		/// </summary>
		/// <param name="record">Where to get the data from.</param>
		/// <returns>BounceInfo filled with data from the data record.</returns>
		private static BounceInfo CreateAndFillBounceInfo(IDataRecord record)
		{
			BounceInfo bounceInfo = new BounceInfo();
			bounceInfo.Count = record.GetInt64("Count");
			bounceInfo.LocalHostname = record.GetString("ip_ipAddress_hostname");
			bounceInfo.LocalIpAddress = record.GetString("ip_ipAddress_ipAddress");
			bounceInfo.Message = record.GetString("mta_transaction_serverResponse");
			bounceInfo.RemoteHostname = record.GetStringOrEmpty("mta_transaction_serverHostname");
			bounceInfo.TransactionStatus = (TransactionStatus)record.GetInt64("mta_transactionStatus_id");
			bounceInfo.LastOccurred = record.GetDateTime("LastOccurred");
			return bounceInfo;
		}

		/// <summary>
		/// Gets a summary of the transactions made in the last one hour.
		/// </summary>
		/// <returns>Transaction Summary</returns>
		public static SendTransactionSummaryCollection GetLastHourTransactionSummary()
		{
			using (SqlConnection conn = MantaDB.GetSqlConnection())
			{
				SqlCommand cmd = conn.CreateCommand();
				cmd.CommandText = @"SELECT [tran].mta_transactionStatus_id, COUNT(*) AS 'Count'
FROM man_mta_transaction as [tran] WITH(nolock)
WHERE [tran].mta_transaction_timestamp >= DATEADD(HOUR, -1, GETUTCDATE())
GROUP BY [tran].mta_transactionStatus_id";
				return new SendTransactionSummaryCollection(DataRetrieval.GetCollectionFromDatabase<SendTransactionSummary>(cmd, CreateAndFillTransactionSummary));
			}
		}

		/// <summary>
		/// Creates a SendTransactionSummary page and fills it with data from the record.
		/// </summary>
		/// <param name="record">Record containing the data to fill with.</param>
		/// <returns>The filled SendTransactionSummary object.</returns>
		private static SendTransactionSummary CreateAndFillTransactionSummary(IDataRecord record)
		{
			return new SendTransactionSummary { 
				Count = record.GetInt64("Count"),
				Status = (TransactionStatus)record.GetInt64("mta_transactionStatus_id")
			};
		}
	}
}
