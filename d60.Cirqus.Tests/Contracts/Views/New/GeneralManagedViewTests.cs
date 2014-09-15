﻿using System;
using System.Linq;
using d60.Cirqus.Events;
using d60.Cirqus.Logging;
using d60.Cirqus.Logging.Console;
using d60.Cirqus.Tests.Contracts.Views.New.Factories;
using d60.Cirqus.Tests.Contracts.Views.New.Models;
using d60.Cirqus.Views.ViewManagers.Locators;
using d60.Cirqus.Views.ViewManagers.New;
using NUnit.Framework;
using TestContext = d60.Cirqus.TestHelpers.TestContext;

namespace d60.Cirqus.Tests.Contracts.Views.New
{
    [TestFixture(typeof(MongoDbManagedViewFactory), Category = TestCategories.MongoDb)]
    [TestFixture(typeof(MsSqlManagedViewFactory), Category = TestCategories.MsSql)]
    public class GeneralManagedViewTests<TFactory> : FixtureBase where TFactory : AbstractManagedViewFactory, new()
    {
        readonly TimeSpan _defaultTimeout = TimeSpan.FromSeconds(5);

        TFactory _factory;
        TestContext _context;

        protected override void DoSetUp()
        {
            CirqusLoggerFactory.Current = new ConsoleLoggerFactory(minLevel:Logger.Level.Debug);

            _factory = new TFactory();

            _context = RegisterForDisposal(new TestContext());
        }

        [Test]
        public void WorksWithSimpleScenario()
        {
            // arrange
            Console.WriteLine("Adding view manager for GeneratedIds");
            var view = _factory.GetManagedView<GeneratedIds>();
            _context.AddViewManager(view);

            // act
            Console.WriteLine("Processing 2 commands");
            _context.ProcessCommand(new GenerateNewId(IdGenerator.InstanceId) { IdBase = "bim" });
            _context.ProcessCommand(new GenerateNewId(IdGenerator.InstanceId) { IdBase = "bim" });
            var last = _context.ProcessCommand(new GenerateNewId(IdGenerator.InstanceId) { IdBase = "bom" });

            // assert
            Console.WriteLine("Waiting until dispatched: {0}", last.GlobalSequenceNumbersOfEmittedEvents.Max());
            view.WaitUntilProcessed(last, _defaultTimeout).Wait();

            var idsView = view.Load(InstancePerAggregateRootLocator.GetViewIdFromAggregateRootId(IdGenerator.InstanceId));

            Assert.That(idsView, Is.Not.Null, "Could not find view!");
            Assert.That(idsView.AllIds.Count, Is.EqualTo(3));
         
            Assert.That(idsView.AllIds, Contains.Item("bim/0"));
            Assert.That(idsView.AllIds, Contains.Item("bim/1"));
            Assert.That(idsView.AllIds, Contains.Item("bom/0"));
        }

        [Test]
        public void AutomaticallyCatchesUpWhenInitializing()
        {
            // arrange
            Console.WriteLine("Processing 2 commands");
            _context.ProcessCommand(new GenerateNewId(IdGenerator.InstanceId) { IdBase = "bim" });
            _context.ProcessCommand(new GenerateNewId(IdGenerator.InstanceId) { IdBase = "bim" });
            var last = _context.ProcessCommand(new GenerateNewId(IdGenerator.InstanceId) { IdBase = "bom" });

            // act
            Console.WriteLine("Adding view manager for GeneratedIds");
            var view = _factory.GetManagedView<GeneratedIds>();
            _context.AddViewManager(view);

            // assert
            Console.WriteLine("Waiting until dispatched: {0}", last.GlobalSequenceNumbersOfEmittedEvents.Max());
            view.WaitUntilProcessed(last, _defaultTimeout).Wait();

            var idsView = view.Load(InstancePerAggregateRootLocator.GetViewIdFromAggregateRootId(IdGenerator.InstanceId));

            Assert.That(idsView, Is.Not.Null, "Could not find view!");
            Assert.That(idsView.AllIds.Count, Is.EqualTo(3));

            Assert.That(idsView.AllIds, Contains.Item("bim/0"));
            Assert.That(idsView.AllIds, Contains.Item("bim/1"));
            Assert.That(idsView.AllIds, Contains.Item("bom/0"));
        }

        [Test]
        public void AutomaticallyCatchesUpAfterPurging()
        {
            // arrange
            Console.WriteLine("Adding view manager for GeneratedIds");
            var view = _factory.GetManagedView<GeneratedIds>();
            _context.AddViewManager(view);

            Console.WriteLine("Processing 2 commands");
            _context.ProcessCommand(new GenerateNewId(IdGenerator.InstanceId) { IdBase = "bim" });
            _context.ProcessCommand(new GenerateNewId(IdGenerator.InstanceId) { IdBase = "bim" });
            var last = _context.ProcessCommand(new GenerateNewId(IdGenerator.InstanceId) { IdBase = "bom" });

            // act
            Console.WriteLine("Purging view");
            _factory.PurgeView<GeneratedIds>();

            // assert
            view.WaitUntilProcessed(last, _defaultTimeout).Wait();

            var idsView = view.Load(InstancePerAggregateRootLocator.GetViewIdFromAggregateRootId(IdGenerator.InstanceId));

            Assert.That(idsView.AllIds.Count, Is.EqualTo(3));

            Assert.That(idsView.AllIds, Contains.Item("bim/0"));
            Assert.That(idsView.AllIds, Contains.Item("bim/1"));
            Assert.That(idsView.AllIds, Contains.Item("bom/0"));
        }

        [Test]
        public void CanManageViewWithLocatorWithMultipleIds()
        {
            // arrange
            const string customHeaderKey = "custom-header";
            var view = _factory.GetManagedView<HeaderCounter>();
            _context.AddViewManager(view);

            // act
            _context.ProcessCommand(new EmitEvent(Guid.NewGuid()) {Meta = {{customHeaderKey, "w00t!"}}});
            _context.ProcessCommand(new EmitEvent(Guid.NewGuid()));
            _context.ProcessCommand(new EmitEvent(Guid.NewGuid()));
            var last = _context.ProcessCommand(new EmitEvent(Guid.NewGuid()));

            // assert
            view.WaitUntilProcessed(last, _defaultTimeout).Wait();

            var batchIdView = LoadCheckForNull(view, DomainEvent.MetadataKeys.BatchId);
            var aggregateRootIdView = LoadCheckForNull(view, DomainEvent.MetadataKeys.AggregateRootId);
            var globalSequenceNumberView = LoadCheckForNull(view, DomainEvent.MetadataKeys.GlobalSequenceNumber);
            var sequenceNumberView = LoadCheckForNull(view, DomainEvent.MetadataKeys.SequenceNumber);
            var customHeaderView = LoadCheckForNull(view, customHeaderKey);

            Assert.That(batchIdView.HeaderValues.Count, Is.EqualTo(4));
            Assert.That(aggregateRootIdView.HeaderValues.Count, Is.EqualTo(4));
            Assert.That(globalSequenceNumberView.HeaderValues.Count, Is.EqualTo(4));
            Assert.That(sequenceNumberView.HeaderValues.Count, Is.EqualTo(1));
            Assert.That(customHeaderView.HeaderValues.Count, Is.EqualTo(1));
        }

        static HeaderCounter LoadCheckForNull(IManagedView<HeaderCounter> view, string metadataKey)
        {
            var loadedView = view.Load(metadataKey);

            if (loadedView == null)
            {
                throw new AssertionException(string.Format("Could not find view with ID '{0}'", metadataKey));
            }

            return loadedView;
        }
    }
}