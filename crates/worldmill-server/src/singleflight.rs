//! Single-flight request coalescing.
//!
//! When several callers ask for the same key at once, only the first (the
//! leader) runs the work; everyone else subscribes to a broadcast channel and
//! receives the leader's result. The work runs in a detached task, so a caller
//! that goes away (e.g. a WebSocket client disconnecting, or a cancelled batch
//! job) never aborts a bake other callers are waiting on.

use std::future::Future;
use std::hash::Hash;
use std::sync::Arc;

use dashmap::DashMap;
use dashmap::mapref::entry::Entry;
use tokio::sync::broadcast;

pub type FlightResult<V> = Result<V, String>;

pub struct SingleFlight<K, V> {
    inflight: Arc<DashMap<K, broadcast::Sender<FlightResult<V>>>>,
}

/// Removes the in-flight entry when the work finishes, panics or is aborted.
struct RemoveGuard<K: Hash + Eq, V> {
    map: Arc<DashMap<K, broadcast::Sender<FlightResult<V>>>>,
    key: Option<K>,
}

impl<K: Hash + Eq, V> Drop for RemoveGuard<K, V> {
    fn drop(&mut self) {
        if let Some(k) = self.key.take() {
            self.map.remove(&k);
        }
    }
}

impl<K, V> SingleFlight<K, V>
where
    K: Hash + Eq + Clone + Send + Sync + 'static,
    V: Clone + Send + Sync + 'static,
{
    pub fn new() -> Self {
        Self {
            inflight: Arc::new(DashMap::new()),
        }
    }

    /// Number of distinct keys currently being worked on.
    pub fn in_flight(&self) -> usize {
        self.inflight.len()
    }

    /// Run `make()` for `key` unless an identical request is already in flight,
    /// in which case wait for and share its result. Returns the result and
    /// whether this call was coalesced onto another caller's work.
    pub async fn run<F, Fut>(&self, key: K, make: F) -> (FlightResult<V>, bool)
    where
        F: FnOnce() -> Fut,
        Fut: Future<Output = FlightResult<V>> + Send + 'static,
    {
        let (mut rx, coalesced) = match self.inflight.entry(key.clone()) {
            Entry::Occupied(e) => (e.get().subscribe(), true),
            Entry::Vacant(v) => {
                let (tx, rx) = broadcast::channel(1);
                v.insert(tx.clone());
                let fut = make();
                let guard = RemoveGuard {
                    map: Arc::clone(&self.inflight),
                    key: Some(key),
                };
                tokio::spawn(async move {
                    let res = fut.await;
                    // Remove the entry *before* publishing: any caller that
                    // subscribed while it was present is guaranteed to get
                    // this message; later callers start a fresh flight.
                    drop(guard);
                    let _ = tx.send(res);
                });
                (rx, false)
            }
        };

        let res = match rx.recv().await {
            Ok(v) => v,
            Err(_) => Err("in-flight request was aborted before completing".to_string()),
        };
        (res, coalesced)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicUsize, Ordering};
    use std::time::Duration;

    #[tokio::test(flavor = "multi_thread", worker_threads = 4)]
    async fn concurrent_identical_requests_run_once() {
        let sf = Arc::new(SingleFlight::<(i32, u32), Arc<Vec<u8>>>::new());
        let runs = Arc::new(AtomicUsize::new(0));

        let mut handles = Vec::new();
        for _ in 0..16 {
            let sf = Arc::clone(&sf);
            let runs = Arc::clone(&runs);
            handles.push(tokio::spawn(async move {
                sf.run((7, 42), move || async move {
                    runs.fetch_add(1, Ordering::SeqCst);
                    tokio::time::sleep(Duration::from_millis(50)).await;
                    Ok(Arc::new(vec![1, 2, 3]))
                })
                .await
            }));
        }

        let mut coalesced = 0;
        for h in handles {
            let (res, was_coalesced) = h.await.unwrap();
            assert_eq!(*res.unwrap(), vec![1, 2, 3]);
            coalesced += was_coalesced as usize;
        }
        assert_eq!(runs.load(Ordering::SeqCst), 1, "work must run exactly once");
        assert_eq!(coalesced, 15);
        assert_eq!(sf.in_flight(), 0);
    }

    #[tokio::test]
    async fn distinct_keys_run_independently_and_errors_propagate() {
        let sf = SingleFlight::<u32, u32>::new();
        let (a, _) = sf.run(1, || async { Ok(10) }).await;
        let (b, _) = sf.run(2, || async { Err("boom".to_string()) }).await;
        assert_eq!(a, Ok(10));
        assert_eq!(b, Err("boom".to_string()));
        // A failed flight is not sticky: the next call re-runs.
        let (c, coalesced) = sf.run(2, || async { Ok(20) }).await;
        assert_eq!(c, Ok(20));
        assert!(!coalesced);
        assert_eq!(sf.in_flight(), 0);
    }

    #[tokio::test]
    async fn panicking_work_releases_waiters() {
        let sf = SingleFlight::<u32, u32>::new();
        let (res, _) = sf
            .run(9, || async {
                panic!("simulated GPU crash");
                #[allow(unreachable_code)]
                Ok(0)
            })
            .await;
        assert!(res.is_err());
        assert_eq!(sf.in_flight(), 0);
    }
}
