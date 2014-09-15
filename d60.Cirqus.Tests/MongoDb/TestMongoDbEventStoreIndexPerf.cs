﻿using System;
using System.Linq;
using d60.Cirqus.Events;
using d60.Cirqus.MongoDb.Events;
using NUnit.Framework;

namespace d60.Cirqus.Tests.MongoDb
{
    [TestFixture]
    [Category(TestCategories.MongoDb)]
    public class TestMongoDbEventStoreIndexPerf : FixtureBase
    {
        [TestCase(true, 100, 1000, Description = "Indexes")]
        [TestCase(false, 100, 1000, Description = "NO indexes")]
        [TestCase(true, 100, 10*1000, Description = "Indexes", Ignore = TestCategories.IgnoreLongRunning)]
        [TestCase(false, 100, 10 * 1000, Description = "NO indexes", Ignore = TestCategories.IgnoreLongRunning)]
        public void IndexSpeedTest(bool useIndexes, int numberOfQueries, int numberOfEvents)
        {
            var database = MongoHelper.InitializeTestDatabase();
            database.Drop();
            var eventStore = new MongoDbEventStore(database, "events", automaticallyCreateIndexes: useIndexes);

            var random = new Random(DateTime.Now.GetHashCode());
            var aggregateRootIds = Enumerable.Range(0, 1000).Select(i => Guid.NewGuid()).ToArray();
            Func<Guid> randomAggregateRootId = () => aggregateRootIds[random.Next(aggregateRootIds.Length)];

            var events = Enumerable.Range(1, numberOfEvents)
                .Select(i => Event(i, randomAggregateRootId()))
                .ToList();

            TakeTime("Insert " + events.Count + " events", () =>
            {
                foreach (var e in events)
                {
                    eventStore.Save(Guid.NewGuid(), new[] {e});
                }
            });

            TakeTime("Execute " + numberOfQueries + " queries", () => numberOfQueries.Times(() => eventStore.Load(randomAggregateRootId(), 0, int.MaxValue).ToList()));
        }

        static DomainEvent Event(int seq, Guid aggregateRootId)
        {
            return new SomeEvent
            {
                SomeValue = "hej",
                Meta =
                {
                    { DomainEvent.MetadataKeys.SequenceNumber, seq },
                    { DomainEvent.MetadataKeys.AggregateRootId, aggregateRootId }
                }
            };
        }

        class SomeEvent : DomainEvent
        {
            public string SomeValue { get; set; }
        }
    }
}