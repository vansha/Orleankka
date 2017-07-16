﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

using Microsoft.Extensions.DependencyInjection;

using Orleans.Providers;
using Orleans.Streams;

namespace Orleankka.Core.Streams
{
    using Utility;
    
    using StreamIdentity = Orleans.Internals.StreamIdentity;

    class StreamSubscriptionMatcher : IStreamProviderImpl
    {
        static readonly Dictionary<string, List<StreamSubscriptionSpecification>> configuration = 
                    new Dictionary<string, List<StreamSubscriptionSpecification>>();

        internal static void Register(IEnumerable<StreamSubscriptionSpecification> specifications)
        {
            foreach (var specification in specifications)
            {
                var existent = configuration.Find(specification.Provider);

                if (existent == null)
                {
                    existent = new List<StreamSubscriptionSpecification>();
                    configuration.Add(specification.Provider, existent);
                }

                existent.Add(specification);
            }
        }

        public static StreamSubscriptionMatch[] Match(IActorSystem system, StreamIdentity stream)
        {
            var specifications = configuration.Find(stream.Provider)
                ?? Enumerable.Empty<StreamSubscriptionSpecification>();

            return Match(system, stream.Id, specifications);
        }

        static StreamSubscriptionMatch[] Match(IActorSystem system, string stream, IEnumerable<StreamSubscriptionSpecification> specifications)
        {
            return specifications
                    .Select(s => s.Match(system, stream))
                    .Where(m => m != StreamSubscriptionMatch.None)
                    .ToArray();
        }

        internal const string TypeKey = "<-::Type::->";

        readonly ConditionalWeakTable<object, object> streams = 
             new ConditionalWeakTable<object, object>();

        IEnumerable<StreamSubscriptionSpecification> specifications;
        IStreamProviderImpl provider;
        IActorSystem system;

        public Task Init(string name, IProviderRuntime runtime, IProviderConfiguration configuration)
        {
            Name = name;

            system = runtime.ServiceProvider.GetRequiredService<IActorSystem>();

            specifications = StreamSubscriptionMatcher.configuration.Find(name)
                ?? Enumerable.Empty<StreamSubscriptionSpecification>();

            var type = Type.GetType(configuration.Properties[TypeKey]);
            Debug.Assert(type != null);

            provider = (IStreamProviderImpl)Activator.CreateInstance(type);
            return provider.Init(name, runtime, configuration);
        }

        public string Name { get; private set; }
        public bool IsRewindable => provider.IsRewindable;

        public Task Start() => provider.Start();
        public Task Close() => provider.Close();

        public IAsyncStream<T> GetStream<T>(Guid unused, string id)
        {
            var stream = provider.GetStream<T>(unused, id);

            return (IAsyncStream<T>) streams.GetValue(stream, _ =>
            {
                var recipients = Match(system, id, specifications);

                Func<T, Task> fan = item => Task.CompletedTask;

                if (recipients.Length > 0)
                    fan = item => Task.WhenAll(recipients.Select(x => x.Receive(item)));

                return new Stream<T>(stream, fan);
            });
        }
    }
}