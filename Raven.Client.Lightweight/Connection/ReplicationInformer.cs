//-----------------------------------------------------------------------
// <copyright file="ReplicationInformer.cs" company="Hibernating Rhinos LTD">
//     Copyright (c) Hibernating Rhinos LTD. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
#if !NETFX_CORE
using System.Net.Sockets;
#endif
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Raven.Abstractions;
using Raven.Abstractions.Data;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Logging;
using Raven.Abstractions.Replication;
using Raven.Abstractions.Util;
#if SILVERLIGHT || NETFX_CORE
using Raven.Client.Connection.Async;
#endif
using Raven.Client.Document;
using Raven.Client.Extensions;
using Raven.Imports.Newtonsoft.Json.Linq;

namespace Raven.Client.Connection
{
	using System.Collections;

	/// <summary>
	/// Replication and failover management on the client side
	/// </summary>
	public class ReplicationInformer : IDisposable
	{
		private readonly ILog log = LogManager.GetCurrentClassLogger();

		private bool firstTime = true;
		protected readonly DocumentConvention conventions;
		private const string RavenReplicationDestinations = "Raven/Replication/Destinations";
		protected DateTime lastReplicationUpdate = DateTime.MinValue;
		private readonly object replicationLock = new object();
		private List<OperationMetadata> replicationDestinations = new List<OperationMetadata>();
		private static readonly List<OperationMetadata> Empty = new List<OperationMetadata>();
		protected static int readStripingBase;

		/// <summary>
		/// Notify when the failover status changed
		/// </summary>
		public event EventHandler<FailoverStatusChangedEventArgs> FailoverStatusChanged = delegate { };

		public List<OperationMetadata> ReplicationDestinations
		{
			get { return replicationDestinations; }
		}

		/// <summary>
		/// Gets the replication destinations.
		/// </summary>
		/// <value>The replication destinations.</value>
		public List<OperationMetadata> ReplicationDestinationsUrls
		{
			get
			{
				if (conventions.FailoverBehavior == FailoverBehavior.FailImmediately)
					return Empty;

				return replicationDestinations
					.Select(operationMetadata => new OperationMetadata(operationMetadata))
					.ToList();
			}
		}

		///<summary>
		/// Create a new instance of this class
		///</summary>
		public ReplicationInformer(DocumentConvention conventions)
		{
			this.conventions = conventions;
		}

#if !SILVERLIGHT
		private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FailureCounter> failureCounts = new System.Collections.Concurrent.ConcurrentDictionary<string, FailureCounter>();
#else
		private readonly Dictionary<string, FailureCounter> failureCounts = new Dictionary<string, FailureCounter>();
#endif

		private Task refreshReplicationInformationTask;

		/// <summary>
		/// Updates the replication information if needed.
		/// </summary>
		/// <param name="serverClient">The server client.</param>
#if SILVERLIGHT || NETFX_CORE
		public Task UpdateReplicationInformationIfNeeded(AsyncServerClient serverClient)
#else
		public Task UpdateReplicationInformationIfNeeded(ServerClient serverClient)
#endif
		{
			if (conventions.FailoverBehavior == FailoverBehavior.FailImmediately)
				return new CompletedTask();

			if (lastReplicationUpdate.AddMinutes(5) > SystemTime.UtcNow)
				return new CompletedTask();

			lock (replicationLock)
			{
				if (firstTime)
				{
					var serverHash = ServerHash.GetServerHash(serverClient.Url);

					var document = ReplicationInformerLocalCache.TryLoadReplicationInformationFromLocalCache(serverHash);
					if (IsInvalidDestinationsDocument(document) == false)
					{
						UpdateReplicationInformationFromDocument(document);
					}
				}

				firstTime = false;

				if (lastReplicationUpdate.AddMinutes(5) > SystemTime.UtcNow)
					return new CompletedTask();

				var taskCopy = refreshReplicationInformationTask;
				if (taskCopy != null)
					return taskCopy;

				return refreshReplicationInformationTask = Task.Factory.StartNew(() => RefreshReplicationInformation(serverClient))
					.ContinueWith(task =>
					{
						if (task.Exception != null)
						{
							log.ErrorException("Failed to refresh replication information", task.Exception);
						}
						refreshReplicationInformationTask = null;
					});
			}
		}

		public class FailureCounter
		{
			public long Value;
			public DateTime LastCheck;
			public bool ForceCheck;

			public FailureCounter()
			{
				LastCheck = SystemTime.UtcNow;
			}
		}


		/// <summary>
		/// Get the current failure count for the url
		/// </summary>
		public long GetFailureCount(string operationUrl)
		{
			return GetHolder(operationUrl).Value;
		}

		/// <summary>
		/// Get failure last check time for the url
		/// </summary>
		public DateTime GetFailureLastCheck(string operationUrl)
		{
			return GetHolder(operationUrl).LastCheck;
		}

		/// <summary>
		/// Should execute the operation using the specified operation URL
		/// </summary>
		public virtual bool ShouldExecuteUsing(string operationUrl, int currentRequest, string method, bool primary)
		{
			if (primary == false)
				AssertValidOperation(method);

			var failureCounter = GetHolder(operationUrl);
			if (failureCounter.Value == 0 || failureCounter.ForceCheck)
			{
				failureCounter.LastCheck = SystemTime.UtcNow;
				return true;
			}

			if (currentRequest % GetCheckRepetitionRate(failureCounter.Value) == 0)
			{
				failureCounter.LastCheck = SystemTime.UtcNow;
				return true;
			}

			if ((SystemTime.UtcNow - failureCounter.LastCheck) > conventions.MaxFailoverCheckPeriod)
			{
				failureCounter.LastCheck = SystemTime.UtcNow;
				return true;
			}

			return false;
		}

		private int GetCheckRepetitionRate(long value)
		{
			if (value < 2)
				return (int)value;
			if (value < 10)
				return 2;
			if (value < 100)
				return 10;
			if (value < 1000)
				return 100;
			if (value < 10000)
				return 1000;
			if (value < 100000)
				return 10000;
			return 100000;
		}

		protected void AssertValidOperation(string method)
		{
			switch (conventions.FailoverBehaviorWithoutFlags)
			{
				case FailoverBehavior.AllowReadsFromSecondaries:
					if (method == "GET")
						return;
					break;
				case FailoverBehavior.AllowReadsFromSecondariesAndWritesToSecondaries:
					return;
				case FailoverBehavior.FailImmediately:
					var allowReadFromAllServers = conventions.FailoverBehavior.HasFlag(FailoverBehavior.ReadFromAllServers);
					if (allowReadFromAllServers && method == "GET")
						return;
					break;
			}
			throw new InvalidOperationException("Could not replicate " + method +
												" operation to secondary node, failover behavior is: " +
												conventions.FailoverBehavior);
		}

		protected FailureCounter GetHolder(string operationUrl)
		{
#if !SILVERLIGHT
			return failureCounts.GetOrAdd(operationUrl, new FailureCounter());
#else
			// need to compensate for 3.5 not having concurrent dic.

			FailureCounter value;
			if (failureCounts.TryGetValue(operationUrl, out value) == false)
			{
				lock (replicationLock)
				{
					if (failureCounts.TryGetValue(operationUrl, out value) == false)
					{
						failureCounts[operationUrl] = value = new FailureCounter();
					}
				}
			}
			return value;
#endif

		}

		/// <summary>
		/// Determines whether this is the first failure on the specified operation URL.
		/// </summary>
		/// <param name="operationUrl">The operation URL.</param>
		public bool IsFirstFailure(string operationUrl)
		{
			FailureCounter value = GetHolder(operationUrl);
			return value.Value == 0;
		}

		/// <summary>
		/// Increments the failure count for the specified operation URL
		/// </summary>
		/// <param name="operationUrl">The operation URL.</param>
		public void IncrementFailureCount(string operationUrl)
		{
			FailureCounter value = GetHolder(operationUrl);
			value.ForceCheck = false;
			var current = Interlocked.Increment(ref value.Value);
			if (current == 1)// first failure
			{
				FailoverStatusChanged(this, new FailoverStatusChangedEventArgs
				{
					Url = operationUrl,
					Failing = true
				});
			}
		}

		private static bool IsInvalidDestinationsDocument(JsonDocument document)
		{
			return document == null ||
				   document.DataAsJson.ContainsKey("Destinations") == false ||
				   document.DataAsJson["Destinations"] == null ||
				   document.DataAsJson["Destinations"].Type == JTokenType.Null;
		}

		/// <summary>
		/// Refreshes the replication information.
		/// Expert use only.
		/// </summary>
#if SILVERLIGHT || NETFX_CORE
		public Task RefreshReplicationInformation(AsyncServerClient commands)
		{
			lock (this)
			{
				var serverHash = ServerHash.GetServerHash(commands.Url);
				return commands.DirectGetAsync(new OperationMetadata(commands.Url), RavenReplicationDestinations).ContinueWith((Task<JsonDocument> getTask) =>
				{
					JsonDocument document;
					if (getTask.Status == TaskStatus.RanToCompletion)
					{
						document = getTask.Result;
						failureCounts[commands.Url] = new FailureCounter(); // we just hit the master, so we can reset its failure count
					}
					else
					{
						log.ErrorException("Could not contact master for new replication information", getTask.Exception);
						document = ReplicationInformerLocalCache.TryLoadReplicationInformationFromLocalCache(serverHash);
					}


					if (IsInvalidDestinationsDocument(document))
					{
						lastReplicationUpdate = SystemTime.UtcNow; // checked and not found
						return;
					}

					ReplicationInformerLocalCache.TrySavingReplicationInformationToLocalCache(serverHash, document);

					UpdateReplicationInformationFromDocument(document);

					lastReplicationUpdate = SystemTime.UtcNow;
				});
			}
		}
#else
		public void RefreshReplicationInformation(ServerClient commands)
		{
			lock (this)
			{
				var serverHash = ServerHash.GetServerHash(commands.Url);

				JsonDocument document;
				try
				{
					document = commands.DirectGet(new OperationMetadata(commands.Url), RavenReplicationDestinations);
					failureCounts[commands.Url] = new FailureCounter(); // we just hit the master, so we can reset its failure count
				}
				catch (Exception e)
				{
					log.ErrorException("Could not contact master for new replication information", e);
					document = ReplicationInformerLocalCache.TryLoadReplicationInformationFromLocalCache(serverHash);
				}
				if (document == null)
				{
					lastReplicationUpdate = SystemTime.UtcNow; // checked and not found
					return;
				}

				ReplicationInformerLocalCache.TrySavingReplicationInformationToLocalCache(serverHash, document);

				UpdateReplicationInformationFromDocument(document);

				lastReplicationUpdate = SystemTime.UtcNow;
			}
		}
#endif

		private void UpdateReplicationInformationFromDocument(JsonDocument document)
		{
			var replicationDocument = document.DataAsJson.JsonDeserialization<ReplicationDocument>();
			replicationDestinations = replicationDocument.Destinations.Select(x =>
			{
				var url = string.IsNullOrEmpty(x.ClientVisibleUrl) ? x.Url : x.ClientVisibleUrl;
				if (string.IsNullOrEmpty(url) || x.Disabled || x.IgnoredClient)
					return null;
				if (string.IsNullOrEmpty(x.Database))
					return new OperationMetadata(url, x.Username, x.Password, x.ApiKey);

				return new OperationMetadata(
					MultiDatabase.GetRootDatabaseUrl(url) + "/databases/" + x.Database + "/",
					x.Username,
					x.Password,
					x.ApiKey);
			})
				// filter out replication destination that don't have the url setup, we don't know how to reach them
				// so we might as well ignore them. Probably private replication destination (using connection string names only)
				.Where(x => x != null)
				.ToList();
			foreach (var replicationDestination in replicationDestinations)
			{
				FailureCounter value;
				if (failureCounts.TryGetValue(replicationDestination.Url, out value))
					continue;
				failureCounts[replicationDestination.Url] = new FailureCounter();
			}
		}

		/// <summary>
		/// Resets the failure count for the specified URL
		/// </summary>
		/// <param name="operationUrl">The operation URL.</param>
		public virtual void ResetFailureCount(string operationUrl)
		{
			var value = GetHolder(operationUrl);
			var oldVal = Interlocked.Exchange(ref value.Value, 0);
			value.LastCheck = SystemTime.UtcNow;
			value.ForceCheck = false;
			if (oldVal != 0)
			{
				FailoverStatusChanged(this,
					new FailoverStatusChangedEventArgs
					{
						Url = operationUrl,
						Failing = false
					});
			}
		}

		public virtual int GetReadStripingBase()
		{
			return Interlocked.Increment(ref readStripingBase);
		}

		#region ExecuteWithReplication

		public virtual T ExecuteWithReplication<T>(string method, string primaryUrl, ICredentials primaryCredentials, int currentRequest, int currentReadStripingBase, Func<OperationMetadata, T> operation)
		{
			T result;
			var timeoutThrown = false;

			var localReplicationDestinations = ReplicationDestinationsUrls; // thread safe copy
			var primaryOperation = new OperationMetadata(primaryUrl, primaryCredentials);

			var shouldReadFromAllServers = conventions.FailoverBehavior.HasFlag(FailoverBehavior.ReadFromAllServers);
			if (shouldReadFromAllServers && method == "GET")
			{
				var replicationIndex = currentReadStripingBase % (localReplicationDestinations.Count + 1);
				// if replicationIndex == destinations count, then we want to use the master
				// if replicationIndex < 0, then we were explicitly instructed to use the master
				if (replicationIndex < localReplicationDestinations.Count && replicationIndex >= 0)
				{
					// if it is failing, ignore that, and move to the master or any of the replicas
					if (ShouldExecuteUsing(localReplicationDestinations[replicationIndex].Url, currentRequest, method, false))
					{
						if (TryOperation(operation, localReplicationDestinations[replicationIndex], primaryOperation, true, out result, out timeoutThrown))
							return result;
					}
				}
			}

			if (ShouldExecuteUsing(primaryOperation.Url, currentRequest, method, true))
			{
				if (TryOperation(operation, primaryOperation, null, !timeoutThrown && localReplicationDestinations.Count > 0, out result, out timeoutThrown))
					return result;
				if (!timeoutThrown && IsFirstFailure(primaryOperation.Url) &&
					TryOperation(operation, primaryOperation, null, localReplicationDestinations.Count > 0, out result, out timeoutThrown))
					return result;
				IncrementFailureCount(primaryOperation.Url);
			}

			for (var i = 0; i < localReplicationDestinations.Count; i++)
			{
				var replicationDestination = localReplicationDestinations[i];
				if (ShouldExecuteUsing(replicationDestination.Url, currentRequest, method, false) == false)
					continue;
				if (TryOperation(operation, replicationDestination, primaryOperation, !timeoutThrown, out result, out timeoutThrown))
					return result;
				if (!timeoutThrown && IsFirstFailure(replicationDestination.Url) &&
					TryOperation(operation, replicationDestination, primaryOperation, localReplicationDestinations.Count > i + 1, out result,
								 out timeoutThrown))
					return result;
				IncrementFailureCount(replicationDestination.Url);
			}
			// this should not be thrown, but since I know the value of should...
			throw new InvalidOperationException(@"Attempted to connect to master and all replicas have failed, giving up.
There is a high probability of a network problem preventing access to all the replicas.
Failed to get in touch with any of the " + (1 + localReplicationDestinations.Count) + " Raven instances.");
		}

		protected virtual bool TryOperation<T>(Func<OperationMetadata, T> operation, OperationMetadata operationMetadata, OperationMetadata primaryOperationMetadata, bool avoidThrowing, out T result, out bool wasTimeout)
		{
			var tryWithPrimaryCredentials = IsFirstFailure(operationMetadata.Url) && primaryOperationMetadata != null;

			try
			{
				
				result = operation(tryWithPrimaryCredentials ? new OperationMetadata(operationMetadata.Url, primaryOperationMetadata.Credentials, primaryOperationMetadata.ApiKey) : operationMetadata);
				ResetFailureCount(operationMetadata.Url);
				wasTimeout = false;
				return true;
			}
			catch (Exception e)
			{
				var webException = e as WebException;
				if (tryWithPrimaryCredentials && operationMetadata.HasCredentials() && webException != null)
				{
					IncrementFailureCount(operationMetadata.Url);

					var response = webException.Response as HttpWebResponse;
					if (response != null && response.StatusCode == HttpStatusCode.Unauthorized)
					{
						return TryOperation(operation, operationMetadata, primaryOperationMetadata, avoidThrowing, out result, out wasTimeout);
					}
				}

				if (avoidThrowing == false)
					throw;
				result = default(T);

				if (IsServerDown(e, out wasTimeout))
				{
					return false;
				}
				throw;
			}
		}
		#endregion

		#region ExecuteWithReplicationAsync

		public Task<T> ExecuteWithReplicationAsync<T>(string method, string primaryUrl, ICredentials primaryCredentials, int currentRequest, int currentReadStripingBase, Func<OperationMetadata, Task<T>> operation)
		{
			return ExecuteWithReplicationAsync(new ExecuteWithReplicationState<T>(method, primaryUrl, primaryCredentials, currentRequest, currentReadStripingBase, operation));
		}

		private Task<T> ExecuteWithReplicationAsync<T>(ExecuteWithReplicationState<T> state)
		{
			var primaryOperation = new OperationMetadata(state.PrimaryUrl, state.PrimaryCredentials);

			switch (state.State)
			{
				case ExecuteWithReplicationStates.Start:
					state.ReplicationDestinations = ReplicationDestinationsUrls;

					var shouldReadFromAllServers = conventions.FailoverBehavior.HasFlag(FailoverBehavior.ReadFromAllServers);
					if (shouldReadFromAllServers && state.Method == "GET")
					{
						var replicationIndex = state.ReadStripingBase % (state.ReplicationDestinations.Count + 1);
						// if replicationIndex == destinations count, then we want to use the master
						// if replicationIndex < 0, then we were explicitly instructed to use the master
						if (replicationIndex < state.ReplicationDestinations.Count && replicationIndex >= 0)
						{
							// if it is failing, ignore that, and move to the master or any of the replicas
							if (ShouldExecuteUsing(state.ReplicationDestinations[replicationIndex].Url, state.CurrentRequest, state.Method, false))
							{
								return AttemptOperationAndOnFailureCallExecuteWithReplication(state.ReplicationDestinations[replicationIndex], primaryOperation,
																							  state.With(ExecuteWithReplicationStates.AfterTryingWithStripedServer),
																							  state.ReplicationDestinations.Count > state.LastAttempt + 1);
							}
						}
					}

					goto case ExecuteWithReplicationStates.AfterTryingWithStripedServer;
				case ExecuteWithReplicationStates.AfterTryingWithStripedServer:

					if (!ShouldExecuteUsing(state.PrimaryUrl, state.CurrentRequest, state.Method, true))
						goto case ExecuteWithReplicationStates.TryAllServers; // skips both checks

					return AttemptOperationAndOnFailureCallExecuteWithReplication(primaryOperation, null,
																					state.With(ExecuteWithReplicationStates.AfterTryingWithDefaultUrl),
																					state.ReplicationDestinations.Count >
																					state.LastAttempt + 1 && !state.TimeoutThrown);

				case ExecuteWithReplicationStates.AfterTryingWithDefaultUrl:
					if (!state.TimeoutThrown && IsFirstFailure(state.PrimaryUrl))
						return AttemptOperationAndOnFailureCallExecuteWithReplication(primaryOperation, null,
																					  state.With(ExecuteWithReplicationStates.AfterTryingWithDefaultUrlTwice),
																					  state.ReplicationDestinations.Count > state.LastAttempt + 1);

					goto case ExecuteWithReplicationStates.AfterTryingWithDefaultUrlTwice;
				case ExecuteWithReplicationStates.AfterTryingWithDefaultUrlTwice:

					IncrementFailureCount(state.PrimaryUrl);

					goto case ExecuteWithReplicationStates.TryAllServers;
				case ExecuteWithReplicationStates.TryAllServers:

					// The following part (cases ExecuteWithReplicationStates.TryAllServers, and ExecuteWithReplicationStates.TryAllServersSecondAttempt)
					// is a for loop, rolled out using goto and nested calls of the method in continuations
					state.LastAttempt++;
					if (state.LastAttempt >= state.ReplicationDestinations.Count)
						goto case ExecuteWithReplicationStates.AfterTryingAllServers;

					var destination = state.ReplicationDestinations[state.LastAttempt];
					if (!ShouldExecuteUsing(destination.ToString(), state.CurrentRequest, state.Method, false))
					{
						// continue the next iteration of the loop
						goto case ExecuteWithReplicationStates.TryAllServers;
					}

					return AttemptOperationAndOnFailureCallExecuteWithReplication(destination, primaryOperation,
																				  state.With(ExecuteWithReplicationStates.TryAllServersSecondAttempt),
																				  state.ReplicationDestinations.Count >
																				  state.LastAttempt + 1 && !state.TimeoutThrown);
				case ExecuteWithReplicationStates.TryAllServersSecondAttempt:
					destination = state.ReplicationDestinations[state.LastAttempt];
					if (!state.TimeoutThrown && IsFirstFailure(destination.Url))
						return AttemptOperationAndOnFailureCallExecuteWithReplication(destination, primaryOperation,
																					  state.With(ExecuteWithReplicationStates.TryAllServersFailedTwice),
																					  state.ReplicationDestinations.Count > state.LastAttempt + 1);

					goto case ExecuteWithReplicationStates.TryAllServersFailedTwice;
				case ExecuteWithReplicationStates.TryAllServersFailedTwice:
					IncrementFailureCount(state.ReplicationDestinations[state.LastAttempt].Url);

					// continue the next iteration of the loop
					goto case ExecuteWithReplicationStates.TryAllServers;

				case ExecuteWithReplicationStates.AfterTryingAllServers:
					throw new InvalidOperationException(@"Attempted to connect to master and all replicas have failed, giving up.
There is a high probability of a network problem preventing access to all the replicas.
Failed to get in touch with any of the " + (1 + state.ReplicationDestinations.Count) + " Raven instances.");

				default:
					throw new InvalidOperationException("Invalid ExecuteWithReplicationState " + state);
			}
		}

		protected virtual Task<T> AttemptOperationAndOnFailureCallExecuteWithReplication<T>(OperationMetadata operationMetadata, OperationMetadata primaryOperationMetadata, ExecuteWithReplicationState<T> state, bool avoidThrowing)
		{
			var tryWithPrimaryCredentials = IsFirstFailure(operationMetadata.Url) && primaryOperationMetadata != null;

			Task<Task<T>> finalTask = state.Operation(tryWithPrimaryCredentials ? new OperationMetadata(operationMetadata.Url, primaryOperationMetadata.Credentials, primaryOperationMetadata.ApiKey) : operationMetadata).ContinueWith(task =>
			{
				switch (task.Status)
				{
					case TaskStatus.RanToCompletion:
						ResetFailureCount(operationMetadata.Url);
						var tcs = new TaskCompletionSource<T>();
						tcs.SetResult(task.Result);
						return tcs.Task;

					case TaskStatus.Canceled:
						tcs = new TaskCompletionSource<T>();
						tcs.SetCanceled();
						return tcs.Task;

					case TaskStatus.Faulted:
						Debug.Assert(task.Exception != null);

						if (task.Exception != null)
						{
							var aggregateException = task.Exception;
							var webException = aggregateException.ExtractSingleInnerException() as WebException;

							if (tryWithPrimaryCredentials && operationMetadata.HasCredentials() && webException != null)
							{
								IncrementFailureCount(operationMetadata.Url);

								var response = webException.Response as HttpWebResponse;
								if (response != null && response.StatusCode == HttpStatusCode.Unauthorized)
								{
									return AttemptOperationAndOnFailureCallExecuteWithReplication(operationMetadata, primaryOperationMetadata, state, avoidThrowing);
								}
							}
						}

						bool timeoutThrown;
						if (IsServerDown(task.Exception, out timeoutThrown) && avoidThrowing)
						{
							state.TimeoutThrown = timeoutThrown;
							return ExecuteWithReplicationAsync(state);
						}

						tcs = new TaskCompletionSource<T>();
						tcs.SetException(task.Exception);
						return tcs.Task;

					default:
						throw new InvalidOperationException("Unknown task status in AttemptOperationAndOnFailureCallExecuteWithReplication");
				}
			});
			return finalTask.Unwrap();
		}

		protected class ExecuteWithReplicationState<T>
		{
			public ExecuteWithReplicationState(string method, string primaryUrl, ICredentials primaryCredentials, int currentRequest, int readStripingBase, Func<OperationMetadata, Task<T>> operation)
			{
				Method = method;
				PrimaryUrl = primaryUrl;
				CurrentRequest = currentRequest;
				ReadStripingBase = readStripingBase;
				Operation = operation;
				PrimaryCredentials = primaryCredentials;

				State = ExecuteWithReplicationStates.Start;
			}

			public readonly string Method;
			public readonly Func<OperationMetadata, Task<T>> Operation;
			public readonly string PrimaryUrl;
			public readonly int CurrentRequest;
			public readonly int ReadStripingBase;
			public readonly ICredentials PrimaryCredentials;

			public ExecuteWithReplicationStates State = ExecuteWithReplicationStates.Start;
			public int LastAttempt = -1;
			public List<OperationMetadata> ReplicationDestinations;
			public bool TimeoutThrown;

			public ExecuteWithReplicationState<T> With(ExecuteWithReplicationStates state)
			{
				State = state;
				return this;
			}
		}

		protected enum ExecuteWithReplicationStates
		{
			Start,
			AfterTryingWithStripedServer,
			AfterTryingWithDefaultUrl,
			TryAllServers,
			AfterTryingAllServers,
			TryAllServersSecondAttempt,
			TryAllServersFailedTwice,
			AfterTryingWithDefaultUrlTwice
		}

		#endregion

		public bool IsHttpStatus(Exception e, params HttpStatusCode[] httpStatusCode)
		{
			var aggregateException = e as AggregateException;
			if (aggregateException != null)
			{
				e = aggregateException.ExtractSingleInnerException();
			}

			var webException = (e as WebException) ?? (e.InnerException as WebException);
			if (webException != null)
			{
				var httpWebResponse = webException.Response as HttpWebResponse;
				if (httpWebResponse != null && httpStatusCode.Contains(httpWebResponse.StatusCode))
					return true;
			}

			return false;
		}

		public virtual bool IsServerDown(Exception e, out bool timeout)
		{
			timeout = false;

			var aggregateException = e as AggregateException;
			if (aggregateException != null)
			{
				e = aggregateException.ExtractSingleInnerException();
			}

			var webException = (e as WebException) ?? (e.InnerException as WebException);
			if (webException != null)
			{
				switch (webException.Status)
				{
#if !SILVERLIGHT && !NETFX_CORE
					case WebExceptionStatus.Timeout:
						timeout = true;
						return true;
					case WebExceptionStatus.NameResolutionFailure:
					case WebExceptionStatus.ReceiveFailure:
					case WebExceptionStatus.PipelineFailure:
					case WebExceptionStatus.ConnectionClosed:

#endif
					case WebExceptionStatus.ConnectFailure:
					case WebExceptionStatus.SendFailure:
						return true;
				}

				var httpWebResponse = webException.Response as HttpWebResponse;
				if (httpWebResponse != null)
				{
					switch (httpWebResponse.StatusCode)
					{
						case HttpStatusCode.RequestTimeout:
						case HttpStatusCode.GatewayTimeout:
							timeout = true;
							return true;
						case HttpStatusCode.BadGateway:
						case HttpStatusCode.ServiceUnavailable:
							return true;
					}
				}
			}
			return
#if !NETFX_CORE
 e.InnerException is SocketException ||
#endif
 e.InnerException is IOException;
		}

		public virtual void Dispose()
		{
			var replicationInformationTaskCopy = refreshReplicationInformationTask;
			if (replicationInformationTaskCopy != null)
				replicationInformationTaskCopy.Wait();
		}

		public void ForceCheck(string primaryUrl, bool shouldForceCheck)
		{
			var failureCounter = this.GetHolder(primaryUrl);
			failureCounter.ForceCheck = shouldForceCheck;
		}
	}

	public class OperationMetadata
	{
		public OperationMetadata(string url, string username = null, string password = null, string apiKey = null)
		{
			Url = url;
			ApiKey = apiKey;

			if (!string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password))
				Credentials = new NetworkCredential(username, password);
		}

		public OperationMetadata(string url, ICredentials credentials, string apiKey = null)
		{
			Url = url;
			ApiKey = apiKey;
			Credentials = credentials;
		}

		public OperationMetadata(OperationMetadata operationMetadata)
		{
			Url = operationMetadata.Url;
			ApiKey = operationMetadata.ApiKey;
			Credentials = operationMetadata.Credentials;
		}

		public string Url { get; private set; }

		public string ApiKey { get; private set; }

		public ICredentials Credentials { get; private set; }

		public bool HasCredentials()
		{
			return !string.IsNullOrEmpty(ApiKey) || Credentials != null;
		}
	}

	/// <summary>
	/// The event arguments for when the failover status changed
	/// </summary>
	public class FailoverStatusChangedEventArgs : EventArgs
	{
		/// <summary>
		/// Whatever that url is now failing
		/// </summary>
		public bool Failing { get; set; }
		/// <summary>
		/// The url whose failover status changed
		/// </summary>
		public string Url { get; set; }
	}
}
