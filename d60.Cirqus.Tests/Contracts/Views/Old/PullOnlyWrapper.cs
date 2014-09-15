using d60.Cirqus.Events;
using d60.Cirqus.Views.ViewManagers;
using d60.Cirqus.Views.ViewManagers.Old;

namespace d60.Cirqus.Tests.Contracts.Views.Old
{
    /// <summary>
    /// Wraps another view manager that is possibly both push/pull-based, ensuring that only the PULL-capable API is used
    /// </summary>
    class PullOnlyWrapper : IPullViewManager
    {
        readonly IPullViewManager _innerPullViewManager;

        public PullOnlyWrapper(IPullViewManager innerPullViewManager)
        {
            _innerPullViewManager = innerPullViewManager;
        }

        public void Initialize(IViewContext context, IEventStore eventStore, bool purgeExistingViews = false)
        {
            _innerPullViewManager.Initialize(context, eventStore, purgeExistingViews);
        }

        public bool Stopped
        {
            get { return _innerPullViewManager.Stopped; }
            set { _innerPullViewManager.Stopped = value; }
        }

        public void CatchUp(IViewContext context, IEventStore eventStore, long lastGlobalSequenceNumber)
        {
            _innerPullViewManager.CatchUp(context, eventStore, lastGlobalSequenceNumber);
        }
    }
}