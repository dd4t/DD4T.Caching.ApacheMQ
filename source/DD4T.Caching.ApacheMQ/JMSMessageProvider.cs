using Apache.NMS;
using Apache.NMS.ActiveMQ;
using DD4T.ContentModel.Contracts.Caching;
using DD4T.ContentModel.Contracts.Configuration;
using DD4T.ContentModel.Contracts.Logging;
using DD4T.Utils.Caching;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace DD4T.Caching.ApacheMQ
{
    /// <summary>
    /// Listener to ApacheMQ messages. 
    /// Keep this object a live
    /// </summary>
    public class JMSMessageProvider : IMessageProvider<ICacheEvent>, IDisposable
    {
        private readonly ILogger _logger;
        private readonly IDD4TConfiguration _configuration;
        private static TimeSpan ReceiveTimeout = TimeSpan.FromDays(2);
        private List<IObserver<ICacheEvent>> _observers;
        private IConnection _connection;

        public JMSMessageProvider(ILogger logger, IDD4TConfiguration configuration)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));


            _logger = logger;
            _configuration = configuration;
            _observers = new List<IObserver<ICacheEvent>>();
        }

        #region properties


        #endregion

        #region IMessageProvider

        public void Start()
        {
            try
            {
                StartConnection();
            }
            catch (NMSConnectionException e)
            {
                _logger.Warning("Unable to connect to JMS service. {0}", e.Message);
            }
            catch (NMSException e)
            {
                _logger.Warning("Unable to connect to JMS service. {0}", e.Message);
            }
        }

        private Uri ConstructUri()
        {
            if (!string.IsNullOrWhiteSpace(_configuration.JMSUrl))
            {
                return new Uri(string.Format("activemq:{0}", _configuration.JMSUrl));
            }
            return new Uri(string.Format("activemq:tcp://{0}:{1}", _configuration.JMSHostname, _configuration.JMSPort));
        }

        private void StartConnection()
        {
            // Connection to apcaheMQ, the connection should stay open! 
            // That means that this object should be kept in memory. 1 way to achieve this, is to register it as a SingleInstance in your DI container
            Uri connecturi = ConstructUri();
            _logger.Debug("About to connect to " + connecturi);

            IConnectionFactory factory = new ConnectionFactory(connecturi);

            _connection = factory.CreateConnection();
            _connection.ClientId = "DD4TJMSListener-" + Guid.NewGuid().ToString();
            _connection.ExceptionListener += connection_ExceptionListener;
            _connection.Start();
            ISession session = _connection.CreateSession(AcknowledgementMode.ClientAcknowledge);
            IDestination destination = session.GetTopic(_configuration.JMSTopic);
            IMessageConsumer consumer = session.CreateConsumer(destination);
            consumer.Listener += new MessageListener(HandleMessage);
        }

        private void connection_ExceptionListener(Exception e)
        {
            _logger.Error("Exception occurred while connecting to JMS", e);
            _logger.Debug("Restarting JMS connection");
            StartConnection();
        }

        protected void HandleMessage(IMessage receivedMsg)
        {
            ITextMessage message = receivedMsg as ITextMessage;

            if (message == null)
            {
                _logger.Warning("received JMS message with id {0} which is not a text message", receivedMsg.NMSMessageId);
                receivedMsg.Acknowledge();
                return;
            }

            _logger.Debug("received text message with id {0} and text {1}", message.NMSMessageId, message.Text);

            try
            {
                ICacheEvent cacheEvent = CacheEventSerializer.Deserialize(message.Text);
                // In Tridion 9, the key sometimes changes from the original format:
                //     1:123:456 (namespace:pubid:itemid)
                // to:
                //     1:123:456:PageMeta (namespace:pubid:itemid:class) -- can also be ComponentMeta, BinaryMeta, etc
                // we will remove that last bit, because the cacheagent doesn't know about it and won't invalidate
                cacheEvent.Key = FixCacheEventKey(cacheEvent.Key);

                foreach (IObserver<ICacheEvent> observer in _observers)
                {
                    observer.OnNext(cacheEvent);
                }
            }
            catch (Exception e)
            {
                _logger.Error("error in invalidation transaction: {0}", e.ToString());
                foreach (IObserver<ICacheEvent> observer in _observers)
                {
                    observer.OnError(e);
                }
            }
            finally
            {
                message.Acknowledge();
                _logger.Debug(string.Format("acknowledged received message with id {0}", message.NMSMessageId));
            }
        }


        #endregion

        #region IObservable
        private class Unsubscriber : IDisposable
        {
            private List<IObserver<ICacheEvent>> _observers;
            private IObserver<ICacheEvent> _observer;

            public Unsubscriber(List<IObserver<ICacheEvent>> observers, IObserver<ICacheEvent> observer)
            {
                this._observers = observers;
                this._observer = observer;
            }

            public void Dispose()
            {
                if (!(_observer == null)) _observers.Remove(_observer);
            }
        }

        public IDisposable Subscribe(IObserver<ICacheEvent> observer)
        {
            if (!_observers.Contains(observer))
                _observers.Add(observer);

            return new Unsubscriber(_observers, observer);
        }

        #endregion

        #region IDisposable
        public void Dispose()
        {
            // Running = false; TODO: check if this is needed
        }
        #endregion

        private static Regex reFixCacheEventKey = new Regex("^([0-9]+:[0-9]+:[0-9]+).*$", RegexOptions.Compiled);

        private static string FixCacheEventKey(string originalKey)
        {
            return reFixCacheEventKey.Replace(originalKey, "$1");
        }
        public class CacheEventSerializer
        {
            private static JsonSerializer _serializer = null;
            private static JsonSerializer Serializer
            {
                get
                {
                    if (_serializer == null)
                    {
                        _serializer = new JsonSerializer();
                    }

                    return _serializer;
                }
            }
            public static ICacheEvent Deserialize(string s)
            {
                using (var stringReader = new StringReader(s))
                {
                    JsonTextReader reader = new JsonTextReader(stringReader);
                    return (ICacheEvent)Serializer.Deserialize<CacheEvent>(reader);
                }
            }
        }
    }
}
