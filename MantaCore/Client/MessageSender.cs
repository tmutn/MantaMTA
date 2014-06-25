﻿using MantaMTA.Core.Client.BO;
using MantaMTA.Core.DNS;
using MantaMTA.Core.Smtp;
using MantaMTA.Core.VirtualMta;
using System;
using System.Collections.Concurrent;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;

namespace MantaMTA.Core.Client
{
	/// <summary>
	/// MessageSender sends Emails to other servers from the Queue.
	/// </summary>
	public class MessageSender : IStopRequired
	{
		#region Singleton
		/// <summary>
		/// The Single instance of this class.
		/// </summary>
		private static MessageSender _Instance = new MessageSender();
		
		/// <summary>
		/// Instance of the MessageSender class.
		/// </summary>
		public static MessageSender Instance
		{
			get
			{
				return MessageSender._Instance;
			}
		}

		private MessageSender()
		{
			MantaCoreEvents.RegisterStopRequiredInstance(this);
		}
		#endregion

		/// <summary>
		/// Holds the maximum amount of Tasks used for sending that should be run at anyone time.
		/// </summary>
		private const int _MAX_SENDING_WORKER_TASKS = 500;

		/// <summary>
		/// Client thread.
		/// </summary>
		private Thread _ClientThread = null;

		/// <summary>
		/// If TRUE then request for client to stop has been made.
		/// </summary>
		private bool _IsStopping = false;

		/// <summary>
		/// IStopRequired method. Will be called by MantaCoreEvents on stopping of MTA.
		/// </summary>
		public void Stop()
		{
			this._IsStopping = true;

			// Hold the stopping thread here while we wait for _ClientThread to stop.
			while (this._ClientThread != null && this._ClientThread.ThreadState != ThreadState.Stopped)
			{
				Thread.Sleep(10);
			}
		}

		/// <summary>
		/// Enqueue a message for delivery.
		/// </summary>
		/// <param name="outboundIP">The IP address that should be used to relay this message.</param>
		/// <param name="mailFrom"></param>
		/// <param name="rcptTo"></param>
		/// <param name="message"></param>
		public void Enqueue(Guid messageID, int ipGroupID, int internalSendID, string mailFrom, string[] rcptTo, string message)
		{
			MtaMessage msg = MtaMessage.Create(messageID, internalSendID, mailFrom, rcptTo);
			msg.Queue(message, ipGroupID);
		}

		/// <summary>
		/// Starts the SMTP Client.
		/// </summary>
		public void Start()
		{
			if (this._ClientThread == null || this._ClientThread.ThreadState != ThreadState.Running)
			{
				this._IsStopping = false;
				this._ClientThread = new Thread(new ThreadStart(delegate
				{
						// Dictionary will hold a single int for each running task. The int means nothing.
					ConcurrentDictionary<Guid, int> runningTasks = new ConcurrentDictionary<Guid, int>();

					Action<MtaQueuedMessage> actSendMessage = delegate(MtaQueuedMessage taskMessage)
					{
							// Generate a unique ID for this task.
						Guid taskID = Guid.NewGuid();

						// Add this task to the running list.
							if (!runningTasks.TryAdd(taskID, 1))
								return;

							Task.Run(new Action(async delegate()
							{
								try
								{
									// Loop while there is a task message to send.
									while (taskMessage != null && !this._IsStopping)
									{
										// Send the message.
										await SendMessageAsync(taskMessage);
										// Dispose of the message.
										taskMessage.Dispose();
										// Try to get another message to send.
										taskMessage = QueueManager.Instance.GetMessageForSending();
									}
								}
								catch (Exception ex)
								{
									if(ex is MaxConnectionsException)
									{
										taskMessage.AttemptSendAfterUtc = DateTime.UtcNow.AddSeconds(2);
										DAL.MtaMessageDB.Save((taskMessage as MtaQueuedMessage));
									}
									else
									{
										// Log if we can't send the message.
										Logging.Debug("Failed to send message", ex);
									}
								}
								finally
								{
									// If there is still a task message then dispose of it.
									if (taskMessage != null)
										taskMessage.Dispose();

									// Remove this task from the dictionary
									int value;
									runningTasks.TryRemove(taskID, out value);
								}
							})); // Always dispose of the queued message.
							};


						// Will hold the current queued message we are working with.
					MtaQueuedMessage queuedMessage = null;
					while (!this._IsStopping) // Run until stop requested
					{
						Action actStartSendingTasks = delegate
						{
							// Loop to create the worker tasks.
							for (int i = runningTasks.Count; i < _MAX_SENDING_WORKER_TASKS; i++)
							{
								// If we don't have a queued message attempt to get one from the queue.
								if (queuedMessage == null)
									queuedMessage = QueueManager.Instance.GetMessageForSending();

								// There are no  more messages to send so exit the loop.
								if (queuedMessage == null)
									break;

								// Don't try and send the message if stop has been issued.
								if (!_IsStopping)
								{
									actSendMessage(queuedMessage);
									queuedMessage = null;
								}
								else // Stop requested, dispose the message without any attempt to send it.
									queuedMessage.Dispose();
							}
						};

						actStartSendingTasks();

						// As long as tasks are running then we should wait here.
						while (runningTasks.Count > 0)
						{
							Thread.Sleep(100);
							if (runningTasks.Count < _MAX_SENDING_WORKER_TASKS)
								actStartSendingTasks();
						}

						// If not stopping get another message to send.
						if (!this._IsStopping)
						{
							queuedMessage = QueueManager.Instance.GetMessageForSending();
								
							// There are no more messages at the moment. Take a nap so not to hammer cpu.
							if (queuedMessage == null)
								Thread.Sleep(1 * 1000);
						}
					}

					// If queued message isn't null dispose of it.
					if (queuedMessage != null)
						queuedMessage.Dispose();
				}));
				this._ClientThread.Start();
			}
		}

		/// <summary>
		/// Sends the specified message.
		/// </summary>
		/// <param name="msg">Message to send</param>
		/// <returns>True if message sent, false if not.</returns>
		private async Task<bool> SendMessageAsync(MtaQueuedMessage msg)
		{
			bool result;
			// Check the message hasn't timed out. If it has don't attempt to send it.
			// Need to do this here as there may be a massive backlog on the server
			// causing messages to be waiting for ages after there AttemptSendAfter
			// before picking up. The MAX_TIME_IN_QUEUE should always be enforced.
			if (msg.AttemptSendAfterUtc - msg.QueuedTimestampUtc > new TimeSpan(0, MtaParameters.MtaMaxTimeInQueue, 0))
			{
				msg.HandleDeliveryFail("Timed out in queue.", null, null);
				result = false;
			}
			else
			{
				string data = string.Empty;
				try
				{
					data = msg.Data;
				}
				catch (Exception)
				{
					msg.HandleDeliveryFail("Email DATA file not found", null, null);
					result = false;
					return result;
				}
				MailAddress mailAddress = msg.RcptTo[0];
				MailAddress mailFrom = msg.MailFrom;
				MXRecord[] mXRecords = DNSManager.GetMXRecords(mailAddress.Host);
				// If mxs is null then there are no MX records.
				if (mXRecords == null || mXRecords.Length < 1)
				{
					msg.HandleDeliveryFail("550 Domain Not Found.", null, null);
					result = false;
				}
				else
				{
					// The IP group that will be used to send the queued message.
					VirtualMtaGroup virtualMtaGroup = VirtualMtaManager.GetVirtualMtaGroup(msg.IPGroupID);
					VirtualMTA sndIpAddress = virtualMtaGroup.GetVirtualMtasForSending(mXRecords[0]);
					SmtpOutboundClient smtpClient = SmtpClientPool.Instance.Dequeue(sndIpAddress, mXRecords, delegate(string message)
					{
						msg.HandleDeliveryDeferral(message, sndIpAddress, null, false);
					}, delegate
					{
						msg.HandleServiceUnavailable(sndIpAddress);
					}, delegate
					{
						msg.HandleDeliveryThrottle(sndIpAddress, null);
					});

					// If no client was dequeued then we can't currently send.
					// This is most likely a max connection issue. Return false but don't
					// log any deferal or throttle.
					if (smtpClient == null)
					{
						result = false;
					}
					else
					{
						try
						{
							Action<string> failedCallback = delegate(string smtpResponse)
							{
								// If smtpRespose starts with 5 then perm error should cause fail
								if (smtpResponse.StartsWith("5"))
									msg.HandleDeliveryFail(smtpResponse, sndIpAddress, smtpClient.MXRecord);
								else
								{
									// If the MX is actively denying use service access, SMTP code 421 then we should inform
									// the ServiceNotAvailableManager manager so it limits our attepts to this MX to 1/minute.
									if (smtpResponse.StartsWith("421"))
									{
										ServiceNotAvailableManager.Add(smtpClient.SmtpStream.LocalAddress.ToString(), smtpClient.MXRecord.Host, DateTime.UtcNow);
										msg.HandleDeliveryDeferral(smtpResponse, sndIpAddress, smtpClient.MXRecord, true);
									}
									else
									{
										// Otherwise message is deferred
										msg.HandleDeliveryDeferral(smtpResponse, sndIpAddress, smtpClient.MXRecord, false);
									}
								}
								throw new MessageSender.SmtpTransactionFailedException();
							};
							// Run each SMTP command after the last.
							await smtpClient.ExecHeloOrRsetAsync(failedCallback);
							await smtpClient.ExecMailFromAsync(mailFrom, failedCallback);
							await smtpClient.ExecRcptToAsync(mailAddress, failedCallback);
							await smtpClient.ExecDataAsync(data, failedCallback);
							SmtpClientPool.Instance.Enqueue(smtpClient);
							msg.HandleDeliverySuccess(sndIpAddress, smtpClient.MXRecord);
							result = true;
						}
						catch (MessageSender.SmtpTransactionFailedException)
						{
							// Exception is thrown to exit transaction, logging of deferrals/failers already handled.
							result = false;
						}
						catch (Exception ex)
						{
							Logging.Error("MessageSender error.", ex);
							if (msg != null)
								msg.HandleDeliveryDeferral("Connection was established but ended abruptly.", sndIpAddress, smtpClient.MXRecord, false);
							result = false;
						}
						finally
						{
							if (smtpClient != null)
							{
								smtpClient.IsActive = false;
								smtpClient.LastActive = DateTime.UtcNow;
							}
						}
					}
				}
			}
			return result;
		}

		/// <summary>
		/// Exception is used to halt SMTP transaction if the server responds with unexpected code.
		/// </summary>
		[Serializable]
		private class SmtpTransactionFailedException : Exception { }
	}

	
}
