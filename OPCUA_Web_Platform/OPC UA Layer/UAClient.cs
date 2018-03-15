﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WebPlatform.Extensions;
using Opc.Ua;
using Opc.Ua.Client;
using WebPlatform.Models.OPCUA;
using WebPlatform.Exceptions;
using WebPlatform.Monitoring;
using WebPlatform.Models.DataSet;

namespace WebPlatform.OPCUALayer
{
    public interface IUaClient
    {
        Task<Node> ReadNodeAsync(string serverUrl, string nodeIdStr);
        Task<Node> ReadNodeAsync(string serverUrl, NodeId nodeId);
        Task<ReferenceDescriptionCollection> BrowseAsync(string serverUrl, string nodeToBrowseIdStr);
        Task<UaValue> ReadUaValueAsync(string serverUrl, VariableNode varNode);
        Task<string> GetDeadBandAsync(string serverUrl, VariableNode varNode);
        Task<bool> WriteNodeValueAsync(string serverUrl, VariableNode variableNode, VariableState state);
        Task<bool> IsFolderTypeAsync(string serverUrlstring, string nodeIdStr);
        Task<bool> isServerAvailable(string serverUrlstring);
        Task<bool[]> CreateMonitoredItemsAsync(string serverUrl, MonitorableNode[] monitorableNodes, string brokerUrl, string topic);
        Task<bool> DeleteMonitoringPublish(string serverUrl, string brokerUrl, string topic);
    }

    public interface IUaClientSingleton : IUaClient {}

    public class UaClient : IUaClientSingleton
    {
        private ApplicationConfiguration _appConfiguration { get; }
        
        //A Dictionary containing al the activ Sessions, indexed per server Id.
        private readonly Dictionary<string, Session> _sessions;
        
        private readonly Dictionary<string, List<MonitorPublishInfo>> _monitorPublishInfo;

        private struct Endpoint
        {
            public int EndpointId { get; set; }
            public string EndpointUrl { get; set; }
            public string SecurityMode { get; set; }
            public string SecurityLevel { get; set; }
            public string SecurityPolicyUri { get; set; }


            public Endpoint(int id, string url, string securityMode, string securityLevel, string securityPolicyUri)
            {
                EndpointId = id;
                EndpointUrl = url;
                SecurityMode = securityMode;
                SecurityLevel = securityLevel;
                SecurityPolicyUri = securityPolicyUri;
            }
        }

        public UaClient()
        {
            _appConfiguration = CreateAppConfiguration("OPCUAWebPlatform", 60000);
            _sessions = new Dictionary<string, Session>();
            _monitorPublishInfo = new Dictionary<string, List<MonitorPublishInfo>>();
        }

        public async Task<Node> ReadNodeAsync(string serverUrl, string nodeIdStr)
        {
            Session session = await GetSessionByUrlAsync(serverUrl);
            NodeId nodeToRead = ParsePlatformNodeIdString(nodeIdStr);
            Node node;
            try
            {
                node = session.ReadNode(nodeToRead);
            }
            catch (ServiceResultException)
            {
                throw new DataSetNotAvailableException();
            }
            return node;
        }

        public async Task<Node> ReadNodeAsync(string serverUrl, NodeId nodeToRead)
        {
            Session session = await GetSessionByUrlAsync(serverUrl);
            Node node;
            try {
                node = session.ReadNode(nodeToRead);
            }
            catch (ServiceResultException)
            {
                throw new DataSetNotAvailableException();
            }
            return node;
        }


        public async Task<bool> WriteNodeValueAsync(string serverUrl, VariableNode variableNode, VariableState state)
        {
            Session session = await GetSessionByUrlAsync(serverUrl);
            var typeManager = new DataTypeManager(session);
            
            //typeManager.GetUaValue(variableNode);
            WriteValueCollection writeValues = new WriteValueCollection();
            
            WriteValue writeValue = new WriteValue
            {
                NodeId = variableNode.NodeId,
                AttributeId = Attributes.Value,
                Value = typeManager.GetDataValueForWriteService(state, variableNode)
            };

            writeValues.Add(writeValue);
            StatusCodeCollection results = new StatusCodeCollection();
            DiagnosticInfoCollection diagnosticInfos = new DiagnosticInfoCollection();
            session.Write(null, writeValues, out results, out diagnosticInfos);
            return StatusCode.IsGood(results[0]);
        }

        public async Task<ReferenceDescriptionCollection> BrowseAsync(string serverUrl, string nodeToBrowseIdStr)
        {
            Session session = await GetSessionByUrlAsync(serverUrl);
            NodeId nodeToBrowseId = ParsePlatformNodeIdString(nodeToBrowseIdStr);

            var browser = new Browser(session)
            {
                NodeClassMask = 0,
                ResultMask = (uint)BrowseResultMask.DisplayName | (uint)BrowseResultMask.NodeClass | (uint)BrowseResultMask.ReferenceTypeInfo,
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HierarchicalReferences
            };

            return browser.Browse(nodeToBrowseId);
        }

        public async Task<bool> IsFolderTypeAsync(string serverUrl, string nodeIdStr)
        {
            Session session = await GetSessionByUrlAsync(serverUrl);
            NodeId nodeToBrowseId = ParsePlatformNodeIdString(nodeIdStr);

            //Set a Browser object to follow HasTypeDefinition Reference only
            var browser = new Browser(session)
            {
                ResultMask = (uint)BrowseResultMask.DisplayName | (uint)BrowseResultMask.TargetInfo,
                BrowseDirection = BrowseDirection.Forward,
                ReferenceTypeId = ReferenceTypeIds.HasTypeDefinition
            };


            ReferenceDescription refDescription = browser.Browse(nodeToBrowseId)[0];
            //NodeId targetId = ExpandedNodeId.ToNodeId(refDescription.NodeId, null);
            NodeId targetId = refDescription.NodeId.ToNodeId();

            //Once got the Object Type, set the browser to follow Type hierarchy in inverse order.
            browser.ReferenceTypeId = ReferenceTypeIds.HasSubtype;
            browser.BrowseDirection = BrowseDirection.Inverse;

            while (targetId != ObjectTypeIds.FolderType && targetId != ObjectTypeIds.BaseObjectType)
            {
                refDescription = browser.Browse(targetId)[0];
                targetId = refDescription.NodeId.ToNodeId();
            }
            return targetId == ObjectTypeIds.FolderType;
        }

        public async Task<UaValue> ReadUaValueAsync(string serverUrl, VariableNode variableNode)
        {
            Session session = await GetSessionByUrlAsync(serverUrl);
            var typeManager = new DataTypeManager(session);

            return typeManager.GetUaValue(variableNode);
        }

        public async Task<bool> isServerAvailable(string serverUrlstring)
        {
            Session session;
            if (!_sessions.ContainsKey(serverUrlstring))
            {
                var endpoints = new List<Endpoint>();
                var endpoint_id = 0;
                try
                {
                    foreach (EndpointDescription s in GetEndpointNames(new Uri(serverUrlstring)))
                    {
                        endpoints.Add(new Endpoint(endpoint_id, s.EndpointUrl, s.SecurityMode.ToString(), s.SecurityLevel.ToString(), s.SecurityPolicyUri));
                        endpoint_id++;
                    }
                }
                catch (Exception exc)
                {
                    return false;
                }
                //TODO: Prende sempre l'endpoint 0, verificare chi o cosa è.
                #warning Indice fisso a 0
                await CreateSessionAsync(serverUrlstring, endpoints[0].EndpointUrl, endpoints[0].SecurityMode, endpoints[0].SecurityPolicyUri);
            }

            session = _sessions[serverUrlstring];
            DataValue serverStatus;
            try
            {
                serverStatus = session.ReadValue(new NodeId(2259, 0));
            }
            catch (Exception exc)
            {
                return await RestoreSessionAsync(serverUrlstring);
            }
            //If StatusCode of the Variable read is not Good or if the Value is not equal to Running (0)
            //the OPC UA Server is not available
            if (DataValue.IsNotGood(serverStatus) || (int)serverStatus.Value != 0)
                return false;
            return true;
        }
        
        public async Task<string> GetDeadBandAsync(string serverUrl, VariableNode varNode)
        {
            Session session = await GetSessionByUrlAsync(serverUrl);
            var dataTypeId = varNode.DataType;

            var browse = new Browser(session)
            {
                ResultMask = (uint) BrowseResultMask.TargetInfo,
                BrowseDirection = BrowseDirection.Inverse,
                ReferenceTypeId = ReferenceTypeIds.HasSubtype
            };
            
            while (!(dataTypeId.Equals(DataTypeIds.Number)) && !(dataTypeId.Equals(DataTypeIds.BaseDataType)))
            {
                //Todo: remove this line when fix the issue related to ExpandedNodeId to nodeId
                //dataTypeId = ExpandedNodeId.ToNodeId(browse.Browse(dataTypeId)[0].NodeId, null);
                dataTypeId = browse.Browse(dataTypeId)[0].NodeId.ToNodeId();
            }

            var isAbsolute = (dataTypeId == DataTypeIds.Number);
            
            browse.BrowseDirection = BrowseDirection.Forward;
            browse.ReferenceTypeId = ReferenceTypeIds.HasProperty;
            var rdc = browse.Browse(varNode.NodeId);

            var isPercent = rdc.Exists(rd => rd.BrowseName.Name.Equals("EURange"));
            
            if (isAbsolute)
            {
                return isPercent ? "Absolute, Percentage" : "Absolute";
            }

            return isPercent ? "Percentage" : "None";

        }

        public async Task<bool[]> CreateMonitoredItemsAsync(string serverUrl, MonitorableNode[] monitorableNodes,
            string brokerUrl, string topic)
        {
            Session session = await GetSessionByUrlAsync(serverUrl);
            
            MonitoredItem mi = null;
            MonitorPublishInfo monitorInfo = null;

            const string pattern = @"^(mqtt|signalr):(.*)$";
            var match = Regex.Match(brokerUrl, pattern);
            var protocol = match.Groups[1].Value;
            var url = match.Groups[2].Value;
            
            var publisher = PublisherFactory.GetPublisherForProtocol(protocol, url);
            
            //Set publishInterval to minimum samplinginterval
            var publishInterval = monitorableNodes.Select(elem => elem.SamplingInterval).Min();

            //Check if a Subscription for the
            if (_monitorPublishInfo.ContainsKey(serverUrl))
            {
                monitorInfo = _monitorPublishInfo[serverUrl].FirstOrDefault(info => info.Topic == topic && info.BrokerUrl == url);
                if (monitorInfo == null)
                {
                    monitorInfo = new MonitorPublishInfo()
                    {
                        Topic = topic,
                        BrokerUrl = url,
                        Subscription = CreateSubscription(serverUrl, session, publishInterval, 0),
                        Publisher = publisher
                    };
                    _monitorPublishInfo[serverUrl].Add(monitorInfo);
                }
                else if (monitorInfo.Subscription.PublishingInterval > publishInterval)
                {
                    monitorInfo.Subscription.PublishingInterval = publishInterval;
                    monitorInfo.Subscription.Modify();
                }
            }
            else
            {
                monitorInfo = new MonitorPublishInfo()
                {
                    Topic = topic,
                    BrokerUrl = url,
                    Subscription = CreateSubscription(serverUrl, session, publishInterval, 0),
                    Publisher = publisher
                };
                var list = new List<MonitorPublishInfo>();
                list.Add(monitorInfo);
                _monitorPublishInfo.Add(serverUrl, list);
            }

            var createdMonitoredItems = new List<MonitoredItem>();

            foreach (var monitorableNode in monitorableNodes)
            {
                mi = new MonitoredItem()
                {
                    StartNodeId = ParsePlatformNodeIdString(monitorableNode.NodeId),
                    DisplayName = monitorableNode.NodeId,
                    SamplingInterval = monitorableNode.SamplingInterval
                };

                if (monitorableNode.DeadBand != "none")
                {
                    mi.Filter = new DataChangeFilter()
                    {
                        Trigger = DataChangeTrigger.StatusValue,
                        DeadbandType = (uint)(DeadbandType)Enum.Parse(typeof(DeadbandType), monitorableNode.DeadBand, true),
                        DeadbandValue = monitorableNode.DeadBandValue
                    };
                }

                mi.Notification += OnMonitorNotification;
                monitorInfo.Subscription.AddItem(mi);
                var monitoredItems = monitorInfo.Subscription.CreateItems();
                createdMonitoredItems.AddRange(monitoredItems);
            }
            
            var results = createdMonitoredItems.Distinct().Select(m => m.Created).ToArray();
            foreach (var monitoredItem in createdMonitoredItems.Where(m => !m.Created))
            {
                monitorInfo.Subscription.RemoveItem(monitoredItem);
            }

            return results;
        }

        public async Task<bool> DeleteMonitoringPublish(string serverUrl, string brokerUrl, string topic)
        {
            Session session = await GetSessionByUrlAsync(serverUrl);

            if (!_monitorPublishInfo.ContainsKey(serverUrl)) return false;
            
            const string pattern = @"^(mqtt|signalr):(.*)$";
            var match = Regex.Match(brokerUrl, pattern);
            brokerUrl = match.Groups[2].Value;
            
            var monitorPublishInfo = _monitorPublishInfo[serverUrl].Find(mpi => mpi.Topic == topic && mpi.BrokerUrl == brokerUrl);

            if (monitorPublishInfo == null) return false;
            
            try
            {
                session.DeleteSubscriptions(null, new UInt32Collection(new[] {monitorPublishInfo.Subscription.Id}), out var results, out var diagnosticInfos);
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                return false;
            }
            
            _monitorPublishInfo[serverUrl].Remove(monitorPublishInfo);
            if (_monitorPublishInfo[serverUrl].Count == 0) _monitorPublishInfo.Remove(serverUrl);
            
            Console.WriteLine($"Deleted Subscription {monitorPublishInfo.Subscription.Id} for the topic {topic}.");
            
            return true;
        }

        #region private methods

        private void OnMonitorNotification(MonitoredItem monitoreditem, MonitoredItemNotificationEventArgs e)
        {
            VariableNode varNode = (VariableNode)monitoreditem.Subscription.Session.ReadNode(monitoreditem.StartNodeId);
            foreach (var value in monitoreditem.DequeueValues())
            {
                Console.WriteLine("Got a value");
                var typeManager = new DataTypeManager(monitoreditem.Subscription.Session);
                UaValue opcvalue = typeManager.GetUaValue(varNode, value, false);

                var monitorInfoPair = _monitorPublishInfo
                    .SelectMany(pair => pair.Value, (parent, child) => new { ServerUrl = parent.Key, Info = child })
                    .First(couple => couple.Info.Subscription == monitoreditem.Subscription);

                var message = $"[TOPIC: {monitorInfoPair.Info.Topic}]  \t ({monitoreditem.DisplayName}): {opcvalue.Value}";
                monitorInfoPair.Info.Forward(message);
                Console.WriteLine(message);
            }
        }

        private Subscription CreateSubscription(string serverUrl, Session session, int publishingInterval, uint maxNotificationPerPublish)
        {
            var sub = new Subscription(session.DefaultSubscription)
            {
                PublishingInterval = publishingInterval,
                MaxNotificationsPerPublish = maxNotificationPerPublish
            };

            if (!session.AddSubscription(sub)) return null;
            sub.Create();
            return sub;

        }

        /// <summary>
        /// This method is called when a OPC UA Service call in a session object returns an error 
        /// </summary>
        /// <param name="serverUrlstring"></param>
        /// <returns></returns>
        private async Task<bool> RestoreSessionAsync(string serverUrlstring)
        {
            _sessions.Remove(serverUrlstring);
            var endpoints = new List<Endpoint>();
            var endpoint_id = 0;
            try
            {
                foreach (EndpointDescription s in GetEndpointNames(new Uri(serverUrlstring)))
                {
                    endpoints.Add(new Endpoint(endpoint_id, s.EndpointUrl, s.SecurityMode.ToString(), s.SecurityLevel.ToString(), s.SecurityPolicyUri));
                    endpoint_id++;
                }
                await CreateSessionAsync(serverUrlstring, endpoints[0].EndpointUrl, endpoints[0].SecurityMode, endpoints[0].SecurityPolicyUri);
            }
            catch (Exception exc)
            {
                return false;
            }
            return true;
        }

        private async Task<Session> GetSessionByUrlAsync(string url)
        {
            if (_sessions.ContainsKey(url))
                return _sessions[url];

            else
            {
                var endpoints = new List<Endpoint>();
                var endpoint_id = 0;
                try
                {
                    foreach (EndpointDescription s in GetEndpointNames(new Uri(url)))
                    {
                        endpoints.Add(new Endpoint(endpoint_id, s.EndpointUrl, s.SecurityMode.ToString(), s.SecurityLevel.ToString(), s.SecurityPolicyUri));
                        endpoint_id++;
                    }
                }
                catch (ServiceResultException exc)
                {
                    switch (exc.StatusCode)
                    {
                        case (StatusCodes.BadNotConnected):
                            throw new DataSetNotAvailableException();
                    }
                }
                //TODO: Prende sempre l'endpoint 0, verificare chi o cosa è.
                #warning Indice fisso a 0
                return await CreateSessionAsync(url, endpoints[0].EndpointUrl, endpoints[0].SecurityMode, endpoints[0].SecurityPolicyUri);

            }
        }

        private async Task<Session> CreateSessionAsync(string serverUrl, string endpointUrl, string securityMode, string securityPolicy)
        {
            await _appConfiguration.Validate(ApplicationType.Client);
            _appConfiguration.CertificateValidator.CertificateValidation += CertificateValidator_CertificateValidation;

            var endpointDescription = new EndpointDescription(endpointUrl);
            endpointDescription.SecurityMode = (MessageSecurityMode)Enum.Parse(typeof(MessageSecurityMode), securityMode, true);
            endpointDescription.SecurityPolicyUri = securityPolicy;

            var endpointConfiguration = EndpointConfiguration.Create(_appConfiguration);

            var endpoint = new ConfiguredEndpoint(endpointDescription.Server, endpointConfiguration);
            endpoint.Update(endpointDescription);

            Console.WriteLine($"Creo la sessione con {endpointDescription.Server} \n\tSecurityMode: {endpointDescription.SecurityMode}\n\tSecurityPolicy: {endpointDescription.SecurityPolicyUri}");

            var s = await Session.Create(_appConfiguration,
                                             endpoint,
                                             true,
                                             false,
                                             _appConfiguration.ApplicationName + "_session",
                                             (uint)_appConfiguration.ClientConfiguration.DefaultSessionTimeout,
                                             null,
                                             null);

            _sessions.Add(serverUrl, s);

            return s;
        }

        private ApplicationConfiguration CreateAppConfiguration(string applicationName, int sessionTimeout)
        {
            var config = new ApplicationConfiguration()
            {
                ApplicationName = applicationName,
                ApplicationType = ApplicationType.Client,
                ApplicationUri = "urn:localhost:OPCFoundation:" + applicationName,
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = "Directory",
                        StorePath = "./OPC Foundation/CertificateStores/MachineDefault",
                        SubjectName = Utils.Format("CN={0}, DC={1}", applicationName, Utils.GetHostName())
                    },
                    TrustedPeerCertificates = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = "./OPC Foundation/CertificateStores/UA Applications",
                    },
                    TrustedIssuerCertificates = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = "./OPC Foundation/CertificateStores/UA Certificate Authorities",
                    },
                    RejectedCertificateStore = new CertificateTrustList
                    {
                        StoreType = "Directory",
                        StorePath = "./OPC Foundation/CertificateStores/RejectedCertificates",
                    },
                    NonceLength = 32,
                    AutoAcceptUntrustedCertificates = true
                },
                TransportConfigurations = new TransportConfigurationCollection(),
                TransportQuotas = new TransportQuotas { OperationTimeout = 15000 },
                ClientConfiguration = new ClientConfiguration { DefaultSessionTimeout = sessionTimeout }
            };

            return config;
        }

        private void CertificateValidator_CertificateValidation(CertificateValidator sender, CertificateValidationEventArgs e)
        {
            Console.WriteLine("Accepted Certificate: {0}", e.Certificate.Subject);
            e.Accept = (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted);
        }

        private EndpointDescriptionCollection GetEndpointNames(Uri serverURI)
        {
            EndpointConfiguration configuration = EndpointConfiguration.Create(_appConfiguration);
            configuration.OperationTimeout = 10;

            using (DiscoveryClient client = DiscoveryClient.Create(serverURI, EndpointConfiguration.Create(_appConfiguration)))
            {
                try
                {
                    EndpointDescriptionCollection endpoints = client.GetEndpoints(null);
                    return endpoints;
                }
                catch (ServiceResultException exc)
                {
                    throw exc;
                }
            }
        }

        private NodeId ParsePlatformNodeIdString(string str)
		{
			const string pattern = @"^(\d+)-(?:(\d+)|(\S+))$";
			var match = Regex.Match(str, pattern);
		    if (match.Success)
		    {
		        var isString = match.Groups[3].Length != 0;
		        var isNumeric = match.Groups[2].Length != 0;
			
		        var idStr = (isString) ? $"s={match.Groups[3]}" : $"i={match.Groups[2]}";
		        var builtStr = $"ns={match.Groups[1]};" + idStr;
			
		        return new NodeId(builtStr);
		    }

		    return null;
		}

        #endregion
    }
}