﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MantaMTA.Core.Client;
using MantaMTA.Core.Enums;
using MantaMTA.Core.Message;

namespace MantaMTA.Core.Server
{
	/// <summary>
	/// Represents an SMTP Server Transaction.
	/// That is a Transaction where we are the Server and someone is sending us stuff.
	/// </summary>
	internal class SmtpServerTransaction
	{
		/// <summary>
		/// The destination for this message.
		/// This should be set to inform us if the message should be put in the drop folder.
		/// Or saved to the database for relaying.
		/// </summary>
		public MessageDestination MessageDestination { get; set; }

		/// <summary>
		/// The mail from.
		/// </summary>
		public string MailFrom
		{
			get
			{
				return _mailFrom;
			}
			set
			{
				_mailFrom = value;
				_hasMailFrom = true;
			}
		}
		public string _mailFrom { get; set; }
		/// <summary>
		/// FALSE until a MailFrom has been set.
		/// </summary>
		public bool HasMailFrom { get { return _hasMailFrom; } }
		private bool _hasMailFrom { get; set; }

		/// <summary>
		/// List of the recipients.
		/// </summary>
		public List<string> RcptTo { get; set; }

		/// <summary>
		/// The message data.
		/// </summary>
		public string Data { get; set; }

		/// <summary>
		/// Holds the Transport MIME used to receive the Data message.
		/// </summary>
		public SmtpTransportMIME TransportMIME { get; set; }

		public SmtpServerTransaction()
		{
			RcptTo = new List<string>();
			MessageDestination = Enums.MessageDestination.Unknown;
			_hasMailFrom = false;
			Data = string.Empty;
			// Default value is set to 8bit as nearly all messages are sent using it.
			// Also some clients will send 8bit messages without passing a BODY parameter.
			TransportMIME = SmtpTransportMIME._8BitUTF;
		}

		/// <summary>
		/// Adds a header to the message data.
		/// </summary>
		/// <param name="name">The header name.</param>
		/// <param name="value">Value for the header.</param>
		public void AddHeader(string name, string value)
		{
			MessageHeaderCollection headers = MessageManager.GetMessageHeaders(Data);
			headers.Insert(0, new MessageHeader(name, value));
			Data = MessageManager.ReplaceHeaders(Data, headers);
		}

		/// <summary>
		/// Save message(s) to DROP folder. Will place files in rcpt sub folder.
		/// OR
		/// Add message to queue for delivery (relay).
		/// </summary>
		public void Save()
		{
			if (MessageDestination == Enums.MessageDestination.Self)
				SaveToLocalMailbox();				
			else if (MessageDestination == Enums.MessageDestination.Relay)
				QueueForRelaying();
			else
				throw new Exception("MessageDestination not set.");

		}

		/// <summary>
		/// Saves the email to the local drop folder.
		/// </summary>
		private void SaveToLocalMailbox()
		{
			// Add the MAIL FROM & RCPT TO headers.
			MessageHeaderCollection headers = MessageManager.GetMessageHeaders(Data);
			headers.Insert(0, new MessageHeader("X-Reciepient", string.Join("; ", RcptTo)));
			if (HasMailFrom && string.IsNullOrWhiteSpace(MailFrom))
				headers.Insert(0, new MessageHeader("X-Sender", "<>"));
			else
				headers.Insert(0, new MessageHeader("X-Sender", MailFrom));
			Data = MessageManager.ReplaceHeaders(Data, headers);

			// Need to drop a copy of the message for each recipient.
			for (int i = 0; i < RcptTo.Count; i++)
			{
				// Put the messages in a subfolder for each recipient.
				// Unless the rcpt to is a return path message in which case put them all in a return-path folder
				string mailDirPath = string.Empty;

				// Bounce.
				if (RcptTo[i].StartsWith("return-", StringComparison.OrdinalIgnoreCase))
					mailDirPath = MtaParameters.BounceDropFolder;

				// Abuse.
				else if (RcptTo[i].StartsWith("abuse@", StringComparison.OrdinalIgnoreCase))
					mailDirPath = MtaParameters.AbuseDropFolder;

				// Postmaster.
				else if (RcptTo[i].StartsWith("postmaster@", StringComparison.OrdinalIgnoreCase))
					mailDirPath = MtaParameters.PostmasterDropFolder;

				// Must be feedback loop.
				else
					mailDirPath = MtaParameters.FeedbackLoopDropFolder;


				// Ensure the directory exists by always calling create.
				Directory.CreateDirectory(mailDirPath);

				// Write the Email File.
				using (StreamWriter sw = new StreamWriter(Path.Combine(mailDirPath, Guid.NewGuid().ToString()) + ".eml"))
				{
					sw.Write(Data);
				}
			}
		}
		/// <summary>
		/// Queues the email for relaying.
		/// </summary>
		private void QueueForRelaying()
		{
			// The email is for relaying.
			Guid messageID = Guid.NewGuid();

			// Look for any MTA control headers.
			MessageHeaderCollection headers = MessageManager.GetMessageHeaders(Data);

			// Will not be null if the SendGroupID header was present.
			MessageHeader ipGroupHeader = headers.SingleOrDefault(m => m.Name.Equals(MessageHeaderNames.SendGroupID, StringComparison.OrdinalIgnoreCase));

			// Parameter will hold the MtaIPGroup that will be used to relay this message.
			VirtualMta.VirtualMtaGroup mtaGroup = null;
			int ipGroupID = 0;
			if (ipGroupHeader != null)
			{
				if (int.TryParse(ipGroupHeader.Value, out ipGroupID))
					mtaGroup = VirtualMta.VirtualMtaManager.GetVirtualMtaGroup(ipGroupID);
			}

			#region Look for a send id, if one doesn't exist create it.
			MessageHeader sendIdHeader = headers.SingleOrDefault(h => h.Name.Equals(MessageHeaderNames.SendID, StringComparison.OrdinalIgnoreCase));
			int internalSendId = -1;
			if (sendIdHeader != null)
			{
				Sends.Send sndID = Sends.SendManager.Instance.GetSend(sendIdHeader.Value);
				if (sndID.SendStatus == SendStatus.Discard)
					throw new SendDiscardingException();
				internalSendId = sndID.InternalID;
			}
			else
			{
				Sends.Send sndID = Sends.SendManager.Instance.GetDefaultInternalSendId();
				if (sndID.SendStatus == SendStatus.Discard)
					throw new SendDiscardingException();
				internalSendId = sndID.InternalID;
			}
			#endregion

			#region Generate Return Path
			string returnPath = string.Empty;

			// Can only return path to messages with one rcpt to
			if (RcptTo.Count == 1)
			{
				// Need to check to see if the message contains a return path overide domain.
				MessageHeader returnPathDomainOverrideHeader = headers.SingleOrDefault(h => h.Name.Equals(MessageHeaderNames.ReturnPathDomain, StringComparison.OrdinalIgnoreCase));

				if (returnPathDomainOverrideHeader != null &&
					MtaParameters.LocalDomains.Count(d => d.Hostname.Equals(returnPathDomainOverrideHeader.Value, StringComparison.OrdinalIgnoreCase)) > 0)
					// The message contained a local domain in the returnpathdomain 
					// header so use it instead of the default.
					returnPath = ReturnPathManager.GenerateReturnPath(RcptTo[0], internalSendId, returnPathDomainOverrideHeader.Value);
				else
					// The message didn't specify a return path overide or it didn't
					// contain a localdomain so use the default.
					returnPath = ReturnPathManager.GenerateReturnPath(RcptTo[0], internalSendId);

				// Insert the return path header.
				headers.Insert(0, new MessageHeader("Return-Path", "<" + returnPath + ">"));
			}
			else
			{
				// multiple rcpt's so can't have unique return paths, use generic mail from.
				returnPath = MailFrom;
			}
			#endregion

			#region Generate a message ID header
			string msgIDHeaderVal = "<" + messageID.ToString("N") + MailFrom.Substring(MailFrom.LastIndexOf("@")) + ">";

			// If there is not message header, add it.
			if (headers.Count(h => h.Name.Equals("Message-ID", StringComparison.OrdinalIgnoreCase)) < 1)
				headers.Add(new MessageHeader("Message-ID", msgIDHeaderVal));
			// Otherwise replace existing message id header with out own.
			else
				headers.Single(h => h.Name.Equals("Message-ID", StringComparison.OrdinalIgnoreCase)).Value = msgIDHeaderVal;
			#endregion

			// Remove any control headers.
			headers = new MessageHeaderCollection(headers.Where(h => !h.Name.StartsWith(MessageHeaderNames.HeaderNamePrefix, StringComparison.OrdinalIgnoreCase)));
			Data = MessageManager.ReplaceHeaders(Data, headers);

			// If the MTA group doesn't exist or it's not got any IPs, use the default.
			if (mtaGroup == null ||
				mtaGroup.VirtualMtaCollection.Count == 0)
				ipGroupID = VirtualMta.VirtualMtaManager.GetDefaultVirtualMtaGroup().ID;

			// Need to put this message in the database for relaying to pickup
			MessageSender.Instance.Enqueue(messageID, ipGroupID, internalSendId, returnPath, RcptTo.ToArray(), Data);
		}
	}
}
