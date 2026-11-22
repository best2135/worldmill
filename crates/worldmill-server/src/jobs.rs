//! Asynchronous batch bake jobs.
//!
//! A job walks a list of chunk coordinates and bakes each one through a
//! caller-supplied closure. Concurrency is bounded by a shared semaphore so
//! batch work never takes every GPU worker slot away from real-time `/ws`
//! streaming. Jobs can be cancelled at any time; cancellation stops dispatching
//! new chunks immediately and drops the job's waiters on in-flight ones (the
//! bakes themselves are single-flighted and keep running to fill the cache).

use std::future::Future;
use std::sync::atomic::{AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Instant;

use dashmap::DashMap;
use serde::Serialize;
use tokio::sync::{Semaphore, watch};
use tokio::task::JoinSet;
use tracing::warn;
use worldmill_core::ChunkCoord;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum JobStatus {
    Queued,
    Running,
    Completed,
    Cancelled,
}

impl JobStatus {
    pub fn is_terminal(self) -> bool {
        matches!(self, JobStatus::Completed | JobStatus::Cancelled)
    }
}

struct JobState {
    status: JobStatus,
    started: Option<Instant>,
    finished: Option<Instant>,
}

pub struct BatchJob {
    pub id: u64,
    pub seed: u32,
    pub lod: u32,
    pub total_chunks: usize,
    completed: AtomicUsize,
    failed: AtomicUsize,
    state: Mutex<JobState>,
    cancel_tx: watch::Sender<bool>,
}

#[derive(Debug, Clone, Serialize)]
pub struct JobSnapshot {
    pub job_id: u64,
    pub status: JobStatus,
    pub seed: u32,
    pub lod: u32,
    pub total_chunks: usize,
    pub completed_chunks: usize,
    pub failed_chunks: usize,
    pub elapsed_ms: f64,
    pub chunks_per_sec: f64,
}

impl BatchJob {
    pub fn new(id: u64, seed: u32, lod: u32, total_chunks: usize) -> Self {
        let (cancel_tx, _) = watch::channel(false);
        Self {
            id,
            seed,
            lod,
            total_chunks,
            completed: AtomicUsize::new(0),
            failed: AtomicUsize::new(0),
            state: Mutex::new(JobState {
                status: JobStatus::Queued,
                started: None,
                finished: None,
            }),
            cancel_tx,
        }
    }

    fn lock(&self) -> std::sync::MutexGuard<'_, JobState> {
        self.state.lock().unwrap_or_else(|e| e.into_inner())
    }

    pub fn status(&self) -> JobStatus {
        self.lock().status
    }

    pub fn completed_chunks(&self) -> usize {
        self.completed.load(Ordering::Relaxed)
    }

    /// `Queued -> Running`. Returns false (and does nothing) from any other state.
    pub fn mark_running(&self) -> bool {
        let mut s = self.lock();
        if s.status != JobStatus::Queued {
            return false;
        }
        s.status = JobStatus::Running;
        s.started = Some(Instant::now());
        true
    }

    /// Request cancellation. A queued or running job becomes `Cancelled`
    /// immediately; a terminal job is left unchanged. Returns the resulting status.
    pub fn cancel(&self) -> JobStatus {
        let mut s = self.lock();
        if s.status.is_terminal() {
            return s.status;
        }
        s.status = JobStatus::Cancelled;
        s.finished = Some(Instant::now());
        drop(s);
        self.cancel_tx.send_replace(true);
        JobStatus::Cancelled
    }

    #[cfg(test)]
    pub fn is_cancelled(&self) -> bool {
        *self.cancel_tx.borrow()
    }

    /// Resolves once cancellation has been requested.
    pub async fn cancelled(&self) {
        let mut rx = self.cancel_tx.subscribe();
        let _ = rx.wait_for(|&c| c).await;
    }

    /// Called by the runner when it stops. Terminal states are sticky, so a job
    /// cancelled concurrently stays `Cancelled`.
    fn finish(&self) -> JobStatus {
        let mut s = self.lock();
        if !s.status.is_terminal() {
            s.status = JobStatus::Completed;
            s.finished = Some(Instant::now());
        }
        s.status
    }

    pub fn snapshot(&self) -> JobSnapshot {
        let (status, elapsed) = {
            let s = self.lock();
            let elapsed = match (s.started, s.finished) {
                (Some(a), Some(b)) => b.saturating_duration_since(a),
                (Some(a), None) => a.elapsed(),
                _ => std::time::Duration::ZERO,
            };
            (s.status, elapsed)
        };
        let completed = self.completed_chunks();
        let elapsed_ms = elapsed.as_secs_f64() * 1000.0;
        let chunks_per_sec = if elapsed_ms > 0.0 {
            completed as f64 / (elapsed_ms / 1000.0)
        } else {
            0.0
        };
        JobSnapshot {
            job_id: self.id,
            status,
            seed: self.seed,
            lod: self.lod,
            total_chunks: self.total_chunks,
            completed_chunks: completed,
            failed_chunks: self.failed.load(Ordering::Relaxed),
            elapsed_ms,
            chunks_per_sec,
        }
    }
}

/// Drive `job` to completion (or cancellation), baking each coordinate with
/// `bake` while holding one permit from `permits`.
pub async fn run_job<F, Fut>(
    job: Arc<BatchJob>,
    coords: Vec<ChunkCoord>,
    permits: Arc<Semaphore>,
    bake: F,
) -> JobStatus
where
    F: Fn(ChunkCoord) -> Fut,
    Fut: Future<Output = Result<(), String>> + Send + 'static,
{
    if !job.mark_running() {
        // Cancelled while still queued.
        return job.status();
    }

    let mut set = JoinSet::new();
    for coord in coords {
        let permit = tokio::select! {
            biased;
            _ = job.cancelled() => break,
            p = Arc::clone(&permits).acquire_owned() => match p {
                Ok(p) => p,
                Err(_) => break, // semaphore closed (shutdown)
            },
        };
        let fut = bake(coord);
        let j = Arc::clone(&job);
        set.spawn(async move {
            let _permit = permit;
            match fut.await {
                Ok(()) => {
                    j.completed.fetch_add(1, Ordering::Relaxed);
                }
                Err(err) => {
                    j.failed.fetch_add(1, Ordering::Relaxed);
                    warn!(job_id = j.id, ?coord, %err, "Batch job chunk bake failed");
                }
            }
        });
        // Reap finished tasks so the set doesn't accumulate.
        while set.try_join_next().is_some() {}
    }

    // Drain in-flight chunks, bailing out promptly on cancellation.
    loop {
        tokio::select! {
            biased;
            _ = job.cancelled() => {
                set.shutdown().await;
                break;
            }
            next = set.join_next() => {
                if next.is_none() {
                    break;
                }
            }
        }
    }

    job.finish()
}

/// In-memory registry of batch jobs. Retains at most `max_retained` jobs,
/// pruning the oldest finished ones first.
pub struct JobRegistry {
    jobs: DashMap<u64, Arc<BatchJob>>,
    next_id: AtomicU64,
    max_retained: usize,
}

impl JobRegistry {
    pub fn new(max_retained: usize) -> Self {
        Self {
            jobs: DashMap::new(),
            next_id: AtomicU64::new(1),
            max_retained: max_retained.max(1),
        }
    }

    pub fn create(&self, seed: u32, lod: u32, total_chunks: usize) -> Arc<BatchJob> {
        let id = self.next_id.fetch_add(1, Ordering::Relaxed);
        let job = Arc::new(BatchJob::new(id, seed, lod, total_chunks));
        self.jobs.insert(id, Arc::clone(&job));
        self.prune();
        job
    }

    pub fn get(&self, id: u64) -> Option<Arc<BatchJob>> {
        self.jobs.get(&id).map(|j| Arc::clone(j.value()))
    }

    /// All retained jobs, newest first.
    pub fn list(&self) -> Vec<Arc<BatchJob>> {
        let mut v: Vec<_> = self.jobs.iter().map(|e| Arc::clone(e.value())).collect();
        v.sort_by_key(|j| std::cmp::Reverse(j.id));
        v
    }

    pub fn active_count(&self) -> usize {
        self.jobs
            .iter()
            .filter(|e| !e.value().status().is_terminal())
            .count()
    }

    fn prune(&self) {
        let excess = self.jobs.len().saturating_sub(self.max_retained);
        if excess == 0 {
            return;
        }
        let mut finished: Vec<u64> = self
            .jobs
            .iter()
            .filter(|e| e.value().status().is_terminal())
            .map(|e| *e.key())
            .collect();
        finished.sort_unstable();
        for id in finished.into_iter().take(excess) {
            self.jobs.remove(&id);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;

    fn coords(n: usize) -> Vec<ChunkCoord> {
        (0..n as i32).map(|i| ChunkCoord::new(i, 0, 0, 0)).collect()
    }

    #[test]
    fn state_transitions() {
        let job = BatchJob::new(1, 42, 0, 10);
        assert_eq!(job.status(), JobStatus::Queued);
        assert_eq!(job.snapshot().elapsed_ms, 0.0);

        assert!(job.mark_running());
        assert!(!job.mark_running(), "Running -> Running must be rejected");
        assert_eq!(job.status(), JobStatus::Running);

        assert_eq!(job.finish(), JobStatus::Completed);
        // Terminal states are sticky.
        assert_eq!(job.cancel(), JobStatus::Completed);
        assert!(!job.is_cancelled());
        assert!(!job.mark_running());

        let job2 = BatchJob::new(2, 42, 0, 10);
        assert_eq!(job2.cancel(), JobStatus::Cancelled);
        assert!(job2.is_cancelled());
        assert!(!job2.mark_running(), "cancelled job must not start");
        assert_eq!(job2.finish(), JobStatus::Cancelled);
    }

    #[tokio::test(flavor = "multi_thread", worker_threads = 4)]
    async fn job_completes_and_respects_concurrency_bound() {
        let job = Arc::new(BatchJob::new(1, 42, 0, 24));
        let permits = Arc::new(Semaphore::new(2));
        let live = Arc::new(AtomicUsize::new(0));
        let peak = Arc::new(AtomicUsize::new(0));

        let (l, p) = (Arc::clone(&live), Arc::clone(&peak));
        let status = run_job(Arc::clone(&job), coords(24), permits, move |_c| {
            let (l, p) = (Arc::clone(&l), Arc::clone(&p));
            async move {
                let now = l.fetch_add(1, Ordering::SeqCst) + 1;
                p.fetch_max(now, Ordering::SeqCst);
                tokio::time::sleep(Duration::from_millis(5)).await;
                l.fetch_sub(1, Ordering::SeqCst);
                Ok(())
            }
        })
        .await;

        assert_eq!(status, JobStatus::Completed);
        let snap = job.snapshot();
        println!("completed job snapshot: {snap:?}");
        assert_eq!(snap.completed_chunks, 24);
        assert_eq!(snap.failed_chunks, 0);
        assert!(snap.elapsed_ms > 0.0);
        assert!(snap.chunks_per_sec > 0.0);
        assert!(peak.load(Ordering::SeqCst) <= 2, "semaphore bound exceeded");
        assert_eq!(peak.load(Ordering::SeqCst), 2);
    }

    #[tokio::test(flavor = "multi_thread", worker_threads = 4)]
    async fn failures_are_counted_but_job_completes() {
        let job = Arc::new(BatchJob::new(1, 42, 0, 6));
        let status = run_job(
            Arc::clone(&job),
            coords(6),
            Arc::new(Semaphore::new(3)),
            |c| async move {
                if c.cx % 2 == 0 {
                    Ok(())
                } else {
                    Err("gpu error".to_string())
                }
            },
        )
        .await;
        assert_eq!(status, JobStatus::Completed);
        let snap = job.snapshot();
        assert_eq!(snap.completed_chunks, 3);
        assert_eq!(snap.failed_chunks, 3);
    }

    #[tokio::test(flavor = "multi_thread", worker_threads = 4)]
    async fn cancelling_running_job_stops_promptly() {
        let job = Arc::new(BatchJob::new(1, 42, 0, 200));
        let permits = Arc::new(Semaphore::new(2));
        let runner = tokio::spawn(run_job(
            Arc::clone(&job),
            coords(200),
            Arc::clone(&permits),
            |_c| async {
                tokio::time::sleep(Duration::from_millis(20)).await;
                Ok(())
            },
        ));

        tokio::time::sleep(Duration::from_millis(70)).await;
        assert_eq!(job.status(), JobStatus::Running);
        assert_eq!(job.cancel(), JobStatus::Cancelled);

        let status = tokio::time::timeout(Duration::from_secs(1), runner)
            .await
            .expect("runner must stop promptly after cancel")
            .unwrap();
        assert_eq!(status, JobStatus::Cancelled);

        let snap = job.snapshot();
        println!("cancelled job snapshot: {snap:?}");
        assert!(snap.completed_chunks > 0);
        assert!(snap.completed_chunks < 200);
        // Elapsed time is frozen once the job is cancelled.
        let frozen = snap.elapsed_ms;
        tokio::time::sleep(Duration::from_millis(20)).await;
        assert_eq!(job.snapshot().elapsed_ms, frozen);
        // All permits are returned to the pool.
        assert_eq!(permits.available_permits(), 2);
    }

    #[tokio::test]
    async fn cancelled_while_queued_never_bakes() {
        let job = Arc::new(BatchJob::new(1, 42, 0, 5));
        job.cancel();
        let baked = Arc::new(AtomicUsize::new(0));
        let b = Arc::clone(&baked);
        let status = run_job(
            Arc::clone(&job),
            coords(5),
            Arc::new(Semaphore::new(1)),
            move |_c| {
                b.fetch_add(1, Ordering::SeqCst);
                async { Ok(()) }
            },
        )
        .await;
        assert_eq!(status, JobStatus::Cancelled);
        assert_eq!(baked.load(Ordering::SeqCst), 0);
    }

    #[test]
    fn registry_prunes_oldest_finished_jobs() {
        let reg = JobRegistry::new(2);
        let a = reg.create(1, 0, 1);
        a.cancel();
        let b = reg.create(1, 0, 1); // still queued (active)
        let c = reg.create(1, 0, 1); // over capacity -> prune `a`
        assert!(reg.get(a.id).is_none());
        assert!(reg.get(b.id).is_some());
        assert!(reg.get(c.id).is_some());
        assert_eq!(reg.active_count(), 2);
        let ids: Vec<u64> = reg.list().iter().map(|j| j.id).collect();
        assert_eq!(ids, vec![c.id, b.id]);
    }
}
