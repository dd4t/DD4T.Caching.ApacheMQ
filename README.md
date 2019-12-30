[![AppVeyor](https://ci.appveyor.com/api/projects/status/github/dd4t/DD4T.Caching.ApacheMQ?branch=master&svg=true&passingText=master)](https://ci.appveyor.com/project/DD4T/dd4t-caching-apachemq)

[![AppVeyor](https://ci.appveyor.com/api/projects/status/github/dd4t/DD4T.Caching.ApacheMQ?branch=develop&svg=true&passingText=develop)](https://ci.appveyor.com/project/DD4T/dd4t-caching-apachemq)
# DD4T.Caching.ApacheMQ
Invalidation of items in the cache when Tridion pages or DCPs are republished or unpublished. Uses the Apache ActiveMQ messaging system.

## Release notes for version 2.5

- Supports Tridion 9 and higher, as well as older versions (6)
- Upgraded Newtonsoft.Json to 11.0.2


## Caching and cache invalidation
DD4T .NET would not work without the help of caching. Any page or component presentation, any link or binary which DD4T retrieves from the Tridion broker, is cached. Normally (if you use the DefaultCacheAgent) all these items are cached in memory by the web application.

If one of these items is changed in the Tridion Content Manager and published, they must be removed from the cache. We call this 'cache invalidation'. The DD4T.Caching.ApacheMQ package allows your DD4T web application to tap in to the messaging system that Tridion itself uses to invalidate the cache of the content service: Apache ActiveMQ. 

## How to set up cache invalidation with DD4T .NET

To make this work, you need to make changes to the Tridion microservices as well as to your web application. We are assuming that you have set up ActiveMQ already, and your deployer service as well as your content / session service are configured with RemoteSynchronization (see [the official documentation](https://docs.sdl.com/LiveContent/content/en-US/SDL%20Tridion%20Sites-v2/GUID-7E728735-073B-4827-AABE-B45592CFF36D)).


### Configure the microservices
Here is what you need to do to configure your deployer and content (or session) services to use a different CacheConnector:

1. Download the latest dd4t-cachechannel-xxx.jar from https://github.com/dd4t/dd4t-cachechannel/releases (make sure you get the jar file that works with your version of Tridion)

2. Copy this jar to the server where your microservices are installed.

3. Go to the deployer service installation and create a folder 'custom' inside the services folder. You can also give it a different name, or put the jar in one of the existing subfolders of the services folder.

4. Copy the dd4t-cachechannel-xxx.jar to the newly created folder.

5. Open cd_storage_conf.xml (in the config folder) in an editor and add the following snippet within the ObjectCache element:

    ``` xml
    <RemoteSynchronization FlushCacheDuringDisconnectInterval="20000" Queuesize="5120" ServiceMonitorInterval="10000">
        <Connector Class="org.dd4t.cache.TextJMSCacheChannelConnector" Topic="Tridion">
            <JndiContext>
                <Property Name="java.naming.factory.initial" Value="org.apache.activemq.jndi.ActiveMQInitialContextFactory"/>
                <Property Name="java.naming.provider.url" Value="tcp://127.0.0.1:61616?soTimeout=5000"/>
                <Property Name="topic.Tridion" Value="TridionStaging"/>
                <Property Name="objectMessageSerializationDefered" Value="true"/>
            </JndiContext>
        </Connector>
    </RemoteSynchronization>
    ```

You may need to change the following parts of the XML:

- The value of the property named 'java.naming.provider.url' should point to your Apache ActiveMQ instance
- The value of the property named 'topic.Tridion' should contain an identifier of your CD environment (e.g. 'staging', 'live', 'dev-staging', etc). The topic is basically the 'feed' that you are subscribing to. The deployer will send messages to this feed, all other clients (content service, your web application) will read from the same feed.

6. If you are hosting your microservices on a Windows machine, you need to uninstall and re-install the deployer service now. On Linux, just restarting it is enough.

7. Go onto the content service (it may also be called session service or session-enabled content service), and repeat steps 3 - 6.


### Configure your web application

1. Add a reference to the NuGet package DD4T.Caching.ApacheMQ to your web project.

2. Look for the following settings in your Web.config (they were added when you added the reference in the previous step):

``` xml
<add key="DD4T.JMS.Hostname" value="my-tridion-server" />
<add key="DD4T.JMS.Port" value="61616" />
<add key="DD4T.JMS.Topic" value="TridionStaging" />
```

3. Make sure the hostname, port and topic are correct. The topic must be the same as the value of 'topic.Tridion' in the configuration of the microservices (see above).

4. Find the HttpApplication in your project (this is the class that starts the entire web application, normally it is in Global.asax.cs).

5. Inside the Application_Start method, add the following code:

``` c#
var dd4tConfiguration = DependencyResolver.Current.GetService<IDD4TConfiguration>();

if (!string.IsNullOrEmpty(dd4tConfiguration.JMSHostname))
{
    var messageProvider = DependencyResolver.Current.GetService<IMessageProvider>() as JMSMessageProvider;
    if (messageProvider != null)
    {
        var cacheAgent = DependencyResolver.Current.GetService<ICacheAgent>();
        if (cacheAgent is DefaultCacheAgent)
        {
            messageProvider.Start();
            ((DefaultCacheAgent)cacheAgent).Subscribe(messageProvider);
        }
    }
}
```  

This starts the message provider (which is responsible for receiving messages from ActiveMQ) and attaches it to the DefaultCacheAgent.










