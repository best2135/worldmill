//! Bounded in-memory LRU cache for encoded chunk blobs.
//!
//! Every access stamps the entry with a monotonically increasing sequence
//! number. A `BTreeMap<seq, key>` index keeps entries ordered by recency so
//! evicting the least-recently-used entry is `O(log n)`. The lock is a plain
//! `std::sync::Mutex` that is never held across an `.await`.

use std::collections::{BTreeMap, HashMap};
use std::hash::Hash;
use std::sync::Mutex;
use std::sync::atomic::{AtomicU64, Ordering};

struct Inner<K, V> {
    /// key -> (value, last-access sequence number)
    map: HashMap<K, (V, u64)>,
    /// last-access sequence number -> key (oldest first)
    order: BTreeMap<u64, K>,
    next_seq: u64,
}

impl<K: Hash + Eq + Clone, V> Inner<K, V> {
    fn bump(&mut self) -> u64 {
        let s = self.next_seq;
        self.next_seq += 1;
        s
    }
}

pub struct LruCache<K, V> {
    inner: Mutex<Inner<K, V>>,
    capacity: usize,
    evictions: AtomicU64,
}

impl<K: Hash + Eq + Clone, V: Clone> LruCache<K, V> {
    /// Create a cache holding at most `capacity` entries (minimum 1).
    pub fn new(capacity: usize) -> Self {
        Self {
            inner: Mutex::new(Inner {
                map: HashMap::new(),
                order: BTreeMap::new(),
                next_seq: 0,
            }),
            capacity: capacity.max(1),
            evictions: AtomicU64::new(0),
        }
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, Inner<K, V>> {
        // A poisoned lock only means another thread panicked mid-operation;
        // the maps are always left consistent, so keep serving.
        self.inner.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// Look up `key`, marking it most-recently-used on a hit.
    pub fn get(&self, key: &K) -> Option<V> {
        let mut g = self.lock();
        let new_seq = g.bump();
        let inner = &mut *g;
        let (value, seq) = inner.map.get_mut(key)?;
        let old_seq = std::mem::replace(seq, new_seq);
        let value = value.clone();
        if let Some(k) = inner.order.remove(&old_seq) {
            inner.order.insert(new_seq, k);
        }
        Some(value)
    }

    /// Check for presence without touching recency.
    #[cfg(test)]
    pub fn contains(&self, key: &K) -> bool {
        self.lock().map.contains_key(key)
    }

    /// Insert or replace `key`, marking it most-recently-used. Evicts the
    /// least-recently-used entries while over capacity and returns how many
    /// entries were evicted.
    pub fn insert(&self, key: K, value: V) -> usize {
        let mut g = self.lock();
        let seq = g.bump();
        let inner = &mut *g;
        if let Some((_, old_seq)) = inner.map.insert(key.clone(), (value, seq)) {
            inner.order.remove(&old_seq);
        }
        inner.order.insert(seq, key);

        let mut evicted = 0;
        while inner.map.len() > self.capacity {
            let Some((_, oldest)) = inner.order.pop_first() else {
                break;
            };
            inner.map.remove(&oldest);
            evicted += 1;
        }
        drop(g);
        if evicted > 0 {
            self.evictions.fetch_add(evicted as u64, Ordering::Relaxed);
        }
        evicted
    }

    pub fn len(&self) -> usize {
        self.lock().map.len()
    }

    pub fn capacity(&self) -> usize {
        self.capacity
    }

    /// Total number of entries evicted since creation.
    pub fn evictions(&self) -> u64 {
        self.evictions.load(Ordering::Relaxed)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stays_bounded_and_evicts_oldest() {
        let c = LruCache::new(3);
        for i in 0..3 {
            assert_eq!(c.insert(i, i * 10), 0);
        }
        assert_eq!(c.len(), 3);

        // Inserting a 4th evicts key 0 (least recently used).
        assert_eq!(c.insert(3, 30), 1);
        assert_eq!(c.len(), 3);
        assert!(!c.contains(&0));
        assert!(c.contains(&1) && c.contains(&2) && c.contains(&3));
        assert_eq!(c.evictions(), 1);
    }

    #[test]
    fn get_refreshes_recency() {
        let c = LruCache::new(3);
        c.insert("a", 1);
        c.insert("b", 2);
        c.insert("c", 3);

        // Touch "a" so "b" becomes the LRU entry.
        assert_eq!(c.get(&"a"), Some(1));
        c.insert("d", 4);
        assert!(c.contains(&"a"));
        assert!(!c.contains(&"b"));

        // Now "c" is the oldest.
        c.insert("e", 5);
        assert!(!c.contains(&"c"));
        assert_eq!(c.evictions(), 2);
        assert_eq!(c.get(&"b"), None);
    }

    #[test]
    fn reinsert_replaces_without_growth() {
        let c = LruCache::new(2);
        c.insert(1, "x");
        c.insert(2, "y");
        // Replacing key 1 refreshes it and must not evict anything.
        assert_eq!(c.insert(1, "x2"), 0);
        assert_eq!(c.len(), 2);
        assert_eq!(c.get(&1), Some("x2"));
        // Key 2 is now LRU.
        c.insert(3, "z");
        assert!(!c.contains(&2));
        assert!(c.contains(&1));
    }

    #[test]
    fn bounded_under_long_flight() {
        // Simulate an endless camera flight: 10k distinct chunks through a
        // 64-entry cache.
        let c = LruCache::new(64);
        for i in 0..10_000u32 {
            c.insert(i, i);
            assert!(c.len() <= 64);
        }
        assert_eq!(c.len(), 64);
        assert_eq!(c.evictions(), 10_000 - 64);
        // Exactly the most recent 64 survive.
        for i in (10_000 - 64)..10_000 {
            assert!(c.contains(&i));
        }
    }

    #[test]
    fn zero_capacity_is_clamped_to_one() {
        let c = LruCache::new(0);
        assert_eq!(c.capacity(), 1);
        c.insert(1, 1);
        c.insert(2, 2);
        assert_eq!(c.len(), 1);
        assert!(c.contains(&2));
    }
}
