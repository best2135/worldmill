//! Authoritative RPG world state: quest/lore catalog, player progression and
//! persistent placed structures.
//!
//! All wire types are deliberately flat (plain structs, strings, numbers and
//! arrays — no maps, no `Option` nulls, no tagged enums) so that Unity's
//! `JsonUtility` can deserialize them 1:1 into `[Serializable]` classes.
//!
//! Mutable state is persisted to a single JSON file with atomic
//! temp-file + `fsync` + rename writes. Every mutation is transactional: it is
//! applied to a copy, persisted, and only then committed to memory, so a failed
//! disk write never leaves memory and disk out of sync.

use std::fs;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, MutexGuard};

use serde::{Deserialize, Serialize};
use tracing::{info, warn};

/// File name of the persisted RPG state inside the cache directory.
pub const RPG_STATE_FILE: &str = "rpg_state.json";
/// Persisted schema version.
const STATE_VERSION: u32 = 1;
/// Upper bound on persisted structures (keeps the state file bounded).
pub const MAX_STRUCTURES: usize = 4096;
/// Upper bound on a structure `kind` identifier length.
const MAX_KIND_LEN: usize = 48;
/// Positions beyond this magnitude are rejected as nonsensical.
const MAX_COORD: f32 = 1.0e6;

// ---------------------------------------------------------------------------
// Wire / persisted types
// ---------------------------------------------------------------------------

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct InventoryItem {
    pub id: String,
    pub name: String,
    /// `"weapon" | "armor" | "trinket" | "material" | "consumable"`.
    pub kind: String,
    pub quantity: u32,
    pub description: String,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct PlayerState {
    pub level: u32,
    pub xp: u32,
    pub xp_to_next: u32,
    pub hp: f32,
    pub max_hp: f32,
    pub stamina: f32,
    pub max_stamina: f32,
    pub equipped_weapon: String,
    pub equipped_armor: String,
    pub inventory: Vec<InventoryItem>,
    /// Empty string once every quest in the catalog is complete.
    pub active_quest_id: String,
    pub completed_quests: Vec<String>,
}

impl Default for PlayerState {
    fn default() -> Self {
        Self {
            level: 1,
            xp: 0,
            xp_to_next: xp_to_next(1),
            hp: max_hp_for(1),
            max_hp: max_hp_for(1),
            stamina: max_stamina_for(1),
            max_stamina: max_stamina_for(1),
            equipped_weapon: "Weathered Iron Axe".to_string(),
            equipped_armor: "Wanderer's Wool Cloak".to_string(),
            inventory: vec![
                InventoryItem {
                    id: "weathered_iron_axe".into(),
                    name: "Weathered Iron Axe".into(),
                    kind: "weapon".into(),
                    quantity: 1,
                    description: "Notched from a hundred winters of felling birch.".into(),
                },
                InventoryItem {
                    id: "wanderers_wool_cloak".into(),
                    name: "Wanderer's Wool Cloak".into(),
                    kind: "armor".into(),
                    quantity: 1,
                    description: "Smells of woodsmoke and wet sheep. Keeps the rain off. Mostly."
                        .into(),
                },
                InventoryItem {
                    id: "timber".into(),
                    name: "Timber".into(),
                    kind: "material".into(),
                    quantity: 20,
                    description: "Rough-split pine, good for walls and floors.".into(),
                },
            ],
            active_quest_id: QUEST_IDS[0].to_string(),
            completed_quests: Vec::new(),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct PlacedStructure {
    pub id: u64,
    pub kind: String,
    pub position: [f32; 3],
    pub rotation_y: f32,
    pub seed: u32,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct LoreEntry {
    pub title: String,
    pub text: String,
}

/// A quest as exposed to clients: static catalog data plus derived `status`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct Quest {
    pub id: String,
    pub title: String,
    pub giver: String,
    pub description: String,
    /// Objective location in Unity/world space (meters, Y-up).
    pub target: [f32; 3],
    pub objectives: Vec<String>,
    pub lore: Vec<LoreEntry>,
    pub reward_xp: u32,
    pub reward_item: InventoryItem,
    /// `"active" | "available" | "completed"`.
    pub status: String,
}

/// Body of `GET /api/rpg/state` (and the base of the quest-completion reply).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct RpgStateView {
    pub player: PlayerState,
    pub quests: Vec<Quest>,
    pub structures: Vec<PlacedStructure>,
}

/// Summary of what a quest completion granted.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct CompletionSummary {
    pub quest_id: String,
    pub xp_gained: u32,
    pub levels_gained: u32,
    pub reward_item: InventoryItem,
}

/// Body of `POST /api/rpg/quests/{id}/complete`: the full state plus a
/// `completion` summary (extra fields are ignored by a plain state parser).
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct QuestCompleteResponse {
    pub player: PlayerState,
    pub quests: Vec<Quest>,
    pub structures: Vec<PlacedStructure>,
    pub completion: CompletionSummary,
}

/// Body of `POST /api/rpg/structures`.
#[derive(Debug, Clone, Deserialize)]
pub struct NewStructure {
    pub kind: String,
    pub position: [f32; 3],
    #[serde(default)]
    pub rotation_y: f32,
    #[serde(default = "default_seed")]
    pub seed: u32,
}

fn default_seed() -> u32 {
    42
}

/// On-disk representation. Quest catalog data is not persisted — it lives in
/// code and quest status is derived from `player.completed_quests`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
struct PersistedState {
    version: u32,
    next_structure_id: u64,
    player: PlayerState,
    structures: Vec<PlacedStructure>,
}

impl Default for PersistedState {
    fn default() -> Self {
        Self {
            version: STATE_VERSION,
            next_structure_id: 1,
            player: PlayerState::default(),
            structures: Vec::new(),
        }
    }
}

// ---------------------------------------------------------------------------
// Errors
// ---------------------------------------------------------------------------

#[derive(Debug)]
pub enum RpgError {
    QuestNotFound(String),
    QuestAlreadyCompleted(String),
    StructureNotFound(u64),
    InvalidStructure(String),
    TooManyStructures,
    Persist(String),
}

impl RpgError {
    /// HTTP status code this error maps to.
    pub fn status_code(&self) -> u16 {
        match self {
            RpgError::QuestNotFound(_) | RpgError::StructureNotFound(_) => 404,
            RpgError::QuestAlreadyCompleted(_) => 409,
            RpgError::InvalidStructure(_) => 400,
            RpgError::TooManyStructures => 422,
            RpgError::Persist(_) => 500,
        }
    }
}

impl std::fmt::Display for RpgError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            RpgError::QuestNotFound(id) => write!(f, "quest '{id}' not found"),
            RpgError::QuestAlreadyCompleted(id) => write!(f, "quest '{id}' is already completed"),
            RpgError::StructureNotFound(id) => write!(f, "structure {id} not found"),
            RpgError::InvalidStructure(msg) => write!(f, "invalid structure: {msg}"),
            RpgError::TooManyStructures => {
                write!(f, "structure limit of {MAX_STRUCTURES} reached")
            }
            RpgError::Persist(msg) => write!(f, "failed to persist RPG state: {msg}"),
        }
    }
}

impl std::error::Error for RpgError {}

// ---------------------------------------------------------------------------
// Progression curve
// ---------------------------------------------------------------------------

/// XP required to advance from `level` to `level + 1`.
pub fn xp_to_next(level: u32) -> u32 {
    100 + level.saturating_sub(1).saturating_mul(50)
}

fn max_hp_for(level: u32) -> f32 {
    100.0 + 15.0 * level.saturating_sub(1) as f32
}

fn max_stamina_for(level: u32) -> f32 {
    100.0 + 10.0 * level.saturating_sub(1) as f32
}

/// Adds XP and applies any resulting level-ups (fully restoring HP/stamina on
/// each). Returns the number of levels gained.
fn grant_xp(player: &mut PlayerState, amount: u32) -> u32 {
    player.xp = player.xp.saturating_add(amount);
    let mut gained = 0;
    while player.xp >= player.xp_to_next {
        player.xp -= player.xp_to_next;
        player.level += 1;
        gained += 1;
        player.xp_to_next = xp_to_next(player.level);
        player.max_hp = max_hp_for(player.level);
        player.max_stamina = max_stamina_for(player.level);
    }
    if gained > 0 {
        player.hp = player.max_hp;
        player.stamina = player.max_stamina;
    }
    gained
}

fn add_item(inventory: &mut Vec<InventoryItem>, item: &InventoryItem) {
    match inventory.iter_mut().find(|i| i.id == item.id) {
        Some(existing) => existing.quantity = existing.quantity.saturating_add(item.quantity),
        None => inventory.push(item.clone()),
    }
}

// ---------------------------------------------------------------------------
// Quest catalog
// ---------------------------------------------------------------------------

const QUEST_IDS: [&str; 3] = ["q_river_crossing", "q_sky_arches", "q_crystal_heart"];

fn lore(title: &str, text: &str) -> LoreEntry {
    LoreEntry {
        title: title.into(),
        text: text.into(),
    }
}

fn strings(items: &[&str]) -> Vec<String> {
    items.iter().map(|s| s.to_string()).collect()
}

/// The static starter quest catalog, in story order. `status` is filled in by
/// [`RpgStore`] from the player's progress.
pub fn quest_catalog() -> Vec<Quest> {
    vec![
        Quest {
            id: QUEST_IDS[0].into(),
            title: "The Sunken Crossing".into(),
            giver: "Aldric the Ferryman".into(),
            description: "The spring thaw came early and angry. Meltwater from the high glaciers \
                swallowed the valley floor in a single night, and the old stone bridge that \
                bound the river villages together now lies beneath a churning grey current. \
                Aldric, whose ferry was smashed against the pilings, swears he saw lantern-light \
                moving under the water where the bridge's keystone should be. Wade out to the \
                drowned crossing, find what keeps the river from receding, and bring word back \
                before the next storm rolls down from the peaks."
                .into(),
            target: [28.0, 4.0, -32.0],
            objectives: strings(&[
                "Speak with Aldric at the ruined ferry landing",
                "Follow the flooded riverbed to the Sunken Crossing",
                "Recover the drowned keystone from beneath the bridge",
                "Return to Aldric before nightfall",
            ]),
            lore: vec![
                lore(
                    "The Bridges of the Old Kings",
                    "Long before the river villages, a forgotten people raised arched bridges of \
                     fitted granite across every ford in the valley, without mortar or iron. \
                     Each keystone is carved with a single rune for 'hold', and the ferrymen say \
                     that while the keystones stand, the river remembers its banks.",
                ),
                lore(
                    "The Drowned Valley",
                    "Elders recall three great floods in living memory. After each, the river \
                     carved a new path, and after each, something old surfaced in the silt: \
                     bronze helms, a longship's prow, a door with no wall around it. Children \
                     are told never to swim where the water runs strangely still.",
                ),
            ],
            reward_xp: 150,
            reward_item: InventoryItem {
                id: "ferrymans_lantern".into(),
                name: "Ferryman's Lantern".into(),
                kind: "trinket".into(),
                quantity: 1,
                description: "A brass lantern that burns blue near running water and never goes \
                              out in the rain."
                    .into(),
            },
            status: String::new(),
        },
        Quest {
            id: QUEST_IDS[1].into(),
            title: "Embers of the Sky Arch".into(),
            giver: "Runekeeper Astrid".into(),
            description: "High above the alpine peaks, where the air is thin enough to taste of \
                iron, great monoliths of black stone hang motionless in the sky, bound together \
                by an arch of smouldering runes. For three nights the embers of the Sky Arch \
                have been dimming, and with each dimming the monoliths sink a little lower \
                toward the villages below. Runekeeper Astrid needs someone sure-footed enough to \
                climb the frozen ridge, rekindle the dying runes, and learn who — or what — \
                has been stealing their fire."
                .into(),
            target: [40.0, 52.0, -10.0],
            objectives: strings(&[
                "Seek Runekeeper Astrid at the mountain shrine",
                "Climb the frozen ridge to the floating monoliths",
                "Rekindle the three dimming rune-embers of the Sky Arch",
                "Confront the ember-thief nesting among the stones",
            ]),
            lore: vec![
                lore(
                    "The Floating Monoliths",
                    "No one living has seen the monoliths move, yet every map drawn of them is \
                     wrong within a generation. Runekeepers believe the stones are the broken \
                     ribs of a sky-ship that carried the first smiths down from the stars, still \
                     straining upward toward home.",
                ),
                lore(
                    "Runefire",
                    "Runes cut by the old masters do not merely glow; they burn with a cold \
                     ember that feeds on intent. A rune carved in anger scorches; one carved in \
                     grief freezes. The Sky Arch was carved, the songs say, in hope — and hope, \
                     unlike timber, does not keep forever.",
                ),
            ],
            reward_xp: 300,
            reward_item: InventoryItem {
                id: "runic_ember_shard".into(),
                name: "Runic Ember Shard".into(),
                kind: "trinket".into(),
                quantity: 1,
                description: "A splinter of the Sky Arch, warm to the touch. Hums faintly when \
                              held toward the heavens."
                    .into(),
            },
            status: String::new(),
        },
        Quest {
            id: QUEST_IDS[2].into(),
            title: "Heart of the Crystal Deep".into(),
            giver: "Hearth-Smith Torin".into(),
            description: "Beneath the roots of the world lie caverns no torch has ever needed to \
                light: whole forests of crystal that pulse with a slow blue bioluminescence, \
                like the breathing of something vast and asleep. Hearth-Smith Torin's forge \
                has gone cold, and he insists only a shard of the Ancient Crystal Golem's heart \
                can fire it again. Descend into the Crystal Deep, survive what guards it, and \
                carry the heart's light back to the surface."
                .into(),
            target: [0.0, -18.0, 0.0],
            objectives: strings(&[
                "Hear Hearth-Smith Torin's tale at his cold forge",
                "Descend into the glowing caverns of the Crystal Deep",
                "Defeat the Ancient Crystal Golem",
                "Return the Heartcrystal to Torin's forge",
            ]),
            lore: vec![
                lore(
                    "The Crystal Deep",
                    "Miners who broke into the Deep spoke of light without flame and of moss \
                     that sang when touched. Few returned twice. Those who did came back with \
                     eyes that shone faintly in the dark, and an inability to sleep anywhere \
                     the earth was silent.",
                ),
                lore(
                    "The Ancient Crystal Golem",
                    "Forged by the first smiths as a warden for their deepest vault, the Golem \
                     was given a heart of living crystal so that it would never tire and never \
                     forget its charge. It has outlived its makers by an age, and it still \
                     guards a door whose purpose no one remembers.",
                ),
                lore(
                    "Hearthfire",
                    "Torin's line has kept the same forge burning for nine generations. Its \
                     fire was first lit, the family claims, with an ember carried up from the \
                     Deep — which would explain why ordinary coal has never been able to \
                     rekindle it.",
                ),
            ],
            reward_xp: 500,
            reward_item: InventoryItem {
                id: "heartcrystal_blade".into(),
                name: "Heartcrystal Blade".into(),
                kind: "weapon".into(),
                quantity: 1,
                description: "A sword quenched in the Golem's living crystal. Its edge glows \
                              brighter the deeper underground you stand."
                    .into(),
            },
            status: String::new(),
        },
    ]
}

/// First catalog quest the player has not completed, or `""` if none remain.
fn next_active_quest(completed: &[String]) -> String {
    QUEST_IDS
        .iter()
        .find(|id| !completed.iter().any(|c| c == *id))
        .map(|s| s.to_string())
        .unwrap_or_default()
}

// ---------------------------------------------------------------------------
// Validation
// ---------------------------------------------------------------------------

fn validate_structure(req: &NewStructure) -> Result<(), RpgError> {
    let kind = req.kind.as_str();
    if kind.is_empty() || kind.len() > MAX_KIND_LEN {
        return Err(RpgError::InvalidStructure(format!(
            "kind must be 1..={MAX_KIND_LEN} characters"
        )));
    }
    if !kind
        .bytes()
        .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || b == b'_')
    {
        return Err(RpgError::InvalidStructure(
            "kind must be lowercase snake_case (a-z, 0-9, _)".into(),
        ));
    }
    if !req.position.iter().all(|v| v.is_finite() && v.abs() <= MAX_COORD) {
        return Err(RpgError::InvalidStructure(
            "position components must be finite and within ±1e6".into(),
        ));
    }
    if !req.rotation_y.is_finite() {
        return Err(RpgError::InvalidStructure("rotation_y must be finite".into()));
    }
    Ok(())
}

// ---------------------------------------------------------------------------
// Store
// ---------------------------------------------------------------------------

/// Thread-safe, disk-persisted authoritative RPG state.
///
/// Operations are synchronous (they hold a mutex across a small file write so
/// that persisted writes are strictly ordered); call them from
/// `tokio::task::spawn_blocking` in async handlers.
pub struct RpgStore {
    path: PathBuf,
    state: Mutex<PersistedState>,
}

impl RpgStore {
    /// Opens (or initializes) the store at `cache_dir/rpg_state.json`.
    ///
    /// A missing file yields fresh default state. An unreadable/corrupt file is
    /// moved aside to `rpg_state.json.corrupt` and replaced with defaults.
    pub fn open(cache_dir: &Path) -> anyhow::Result<Self> {
        fs::create_dir_all(cache_dir)?;
        let path = cache_dir.join(RPG_STATE_FILE);
        let state = match fs::read(&path) {
            Ok(raw) => match serde_json::from_slice::<PersistedState>(&raw) {
                Ok(mut s) => {
                    sanitize(&mut s);
                    info!(
                        path = %path.display(),
                        level = s.player.level,
                        structures = s.structures.len(),
                        "Loaded persisted RPG state"
                    );
                    s
                }
                Err(err) => {
                    let backup = path.with_extension("json.corrupt");
                    warn!(%err, backup = %backup.display(), "Corrupt RPG state; resetting to defaults");
                    let _ = fs::rename(&path, &backup);
                    PersistedState::default()
                }
            },
            Err(err) if err.kind() == std::io::ErrorKind::NotFound => {
                info!(path = %path.display(), "No RPG state on disk; starting fresh");
                PersistedState::default()
            }
            Err(err) => return Err(err.into()),
        };
        let store = Self {
            path,
            state: Mutex::new(state),
        };
        // Make sure a valid file exists from the start.
        {
            let guard = store.lock();
            write_atomic(&store.path, &guard).map_err(|e| anyhow::anyhow!("{e}"))?;
        }
        Ok(store)
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    fn lock(&self) -> MutexGuard<'_, PersistedState> {
        self.state.lock().unwrap_or_else(|e| e.into_inner())
    }

    /// Applies `f` to a copy of the state, persists the copy, and only then
    /// commits it. Memory and disk never diverge on a failed write.
    fn transact<T>(
        &self,
        f: impl FnOnce(&mut PersistedState) -> Result<T, RpgError>,
    ) -> Result<(T, RpgStateView), RpgError> {
        let mut guard = self.lock();
        let mut next = guard.clone();
        let out = f(&mut next)?;
        write_atomic(&self.path, &next)?;
        *guard = next;
        Ok((out, view_of(&guard)))
    }

    /// `GET /api/rpg/state`.
    pub fn snapshot(&self) -> RpgStateView {
        view_of(&self.lock())
    }

    /// Completes a quest: grants XP (with level-ups), adds the reward item,
    /// advances `active_quest_id`, and persists.
    pub fn complete_quest(&self, quest_id: &str) -> Result<QuestCompleteResponse, RpgError> {
        let quest = quest_catalog()
            .into_iter()
            .find(|q| q.id == quest_id)
            .ok_or_else(|| RpgError::QuestNotFound(quest_id.to_string()))?;

        let (completion, view) = self.transact(|s| {
            let p = &mut s.player;
            if p.completed_quests.iter().any(|c| c == quest_id) {
                return Err(RpgError::QuestAlreadyCompleted(quest_id.to_string()));
            }
            p.completed_quests.push(quest.id.clone());
            let levels_gained = grant_xp(p, quest.reward_xp);
            add_item(&mut p.inventory, &quest.reward_item);
            if quest.reward_item.kind == "weapon" {
                p.equipped_weapon = quest.reward_item.name.clone();
            } else if quest.reward_item.kind == "armor" {
                p.equipped_armor = quest.reward_item.name.clone();
            }
            p.active_quest_id = next_active_quest(&p.completed_quests);
            Ok(CompletionSummary {
                quest_id: quest.id.clone(),
                xp_gained: quest.reward_xp,
                levels_gained,
                reward_item: quest.reward_item.clone(),
            })
        })?;

        info!(
            quest = quest_id,
            level = view.player.level,
            levels_gained = completion.levels_gained,
            next = %view.player.active_quest_id,
            "Quest completed"
        );
        Ok(QuestCompleteResponse {
            player: view.player,
            quests: view.quests,
            structures: view.structures,
            completion,
        })
    }

    /// Places a structure with a fresh monotonic id and persists.
    pub fn add_structure(&self, req: NewStructure) -> Result<PlacedStructure, RpgError> {
        validate_structure(&req)?;
        let (structure, _) = self.transact(|s| {
            if s.structures.len() >= MAX_STRUCTURES {
                return Err(RpgError::TooManyStructures);
            }
            let structure = PlacedStructure {
                id: s.next_structure_id,
                kind: req.kind,
                position: req.position,
                rotation_y: req.rotation_y,
                seed: req.seed,
            };
            s.next_structure_id += 1;
            s.structures.push(structure.clone());
            Ok(structure)
        })?;
        info!(id = structure.id, kind = %structure.kind, seed = structure.seed, "Structure placed");
        Ok(structure)
    }

    /// Removes a structure by id and persists. Ids are never reused.
    pub fn remove_structure(&self, id: u64) -> Result<PlacedStructure, RpgError> {
        let (removed, _) = self.transact(|s| {
            let idx = s
                .structures
                .iter()
                .position(|st| st.id == id)
                .ok_or(RpgError::StructureNotFound(id))?;
            Ok(s.structures.remove(idx))
        })?;
        info!(id, kind = %removed.kind, "Structure removed");
        Ok(removed)
    }

    /// Removes all structures (or only those for `seed`) and persists.
    /// Returns the number removed.
    pub fn clear_structures(&self, seed: Option<u32>) -> Result<usize, RpgError> {
        let (removed, _) = self.transact(|s| {
            let before = s.structures.len();
            match seed {
                Some(seed) => s.structures.retain(|st| st.seed != seed),
                None => s.structures.clear(),
            }
            Ok(before - s.structures.len())
        })?;
        info!(?seed, removed, "Structures cleared");
        Ok(removed)
    }
}

fn view_of(s: &PersistedState) -> RpgStateView {
    let p = &s.player;
    let quests = quest_catalog()
        .into_iter()
        .map(|mut q| {
            q.status = if p.completed_quests.contains(&q.id) {
                "completed"
            } else if p.active_quest_id == q.id {
                "active"
            } else {
                "available"
            }
            .to_string();
            q
        })
        .collect();
    RpgStateView {
        player: p.clone(),
        quests,
        structures: s.structures.clone(),
    }
}

/// Repairs invariants of state loaded from disk (hand edits, older versions).
fn sanitize(s: &mut PersistedState) {
    s.version = STATE_VERSION;
    let max_id = s.structures.iter().map(|st| st.id).max().unwrap_or(0);
    s.next_structure_id = s.next_structure_id.max(max_id + 1).max(1);

    let p = &mut s.player;
    p.level = p.level.max(1);
    p.xp_to_next = xp_to_next(p.level);
    p.max_hp = p.max_hp.max(1.0);
    p.max_stamina = p.max_stamina.max(1.0);
    p.hp = p.hp.clamp(0.0, p.max_hp);
    p.stamina = p.stamina.clamp(0.0, p.max_stamina);
    let mut seen = Vec::new();
    p.completed_quests.retain(|q| {
        let keep = QUEST_IDS.contains(&q.as_str()) && !seen.contains(q);
        if keep {
            seen.push(q.clone());
        }
        keep
    });
    let active_ok = QUEST_IDS.contains(&p.active_quest_id.as_str())
        && !p.completed_quests.contains(&p.active_quest_id);
    if !active_ok {
        p.active_quest_id = next_active_quest(&p.completed_quests);
    }
}

/// Atomically replaces `path` with the JSON encoding of `state`:
/// write temp file -> fsync -> rename over the original.
fn write_atomic(path: &Path, state: &PersistedState) -> Result<(), RpgError> {
    let persist = |e: std::io::Error| RpgError::Persist(e.to_string());
    let json = serde_json::to_vec_pretty(state).map_err(|e| RpgError::Persist(e.to_string()))?;
    let tmp = path.with_extension("json.tmp");
    {
        let mut f = fs::File::create(&tmp).map_err(persist)?;
        f.write_all(&json).map_err(persist)?;
        f.sync_all().map_err(persist)?;
    }
    fs::rename(&tmp, path).map_err(|e| {
        let _ = fs::remove_file(&tmp);
        persist(e)
    })
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicU32, Ordering};

    /// A unique, self-cleaning temporary directory.
    struct TempDir(PathBuf);

    impl TempDir {
        fn new(tag: &str) -> Self {
            static COUNTER: AtomicU32 = AtomicU32::new(0);
            let nanos = std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos();
            let dir = std::env::temp_dir().join(format!(
                "worldmill-rpg-{tag}-{}-{}-{nanos}",
                std::process::id(),
                COUNTER.fetch_add(1, Ordering::Relaxed)
            ));
            fs::create_dir_all(&dir).unwrap();
            Self(dir)
        }
    }

    impl Drop for TempDir {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    fn campfire(x: f32) -> NewStructure {
        NewStructure {
            kind: "campfire".into(),
            position: [x, 4.5, -3.0],
            rotation_y: 0.5,
            seed: 42,
        }
    }

    #[test]
    fn fresh_store_has_seeded_catalog_and_default_player() {
        let dir = TempDir::new("fresh");
        let store = RpgStore::open(&dir.0).unwrap();
        let s = store.snapshot();

        assert!(store.path().is_file(), "state file written on open");
        assert_eq!(s.player.level, 1);
        assert_eq!(s.player.xp, 0);
        assert_eq!(s.player.xp_to_next, 100);
        assert_eq!(s.player.active_quest_id, "q_river_crossing");
        assert!(s.structures.is_empty());

        let ids: Vec<_> = s.quests.iter().map(|q| q.id.as_str()).collect();
        assert_eq!(ids, ["q_river_crossing", "q_sky_arches", "q_crystal_heart"]);
        let statuses: Vec<_> = s.quests.iter().map(|q| q.status.as_str()).collect();
        assert_eq!(statuses, ["active", "available", "available"]);

        let q = &s.quests[0];
        assert_eq!(q.title, "The Sunken Crossing");
        assert_eq!(q.giver, "Aldric the Ferryman");
        assert_eq!(q.target, [28.0, 4.0, -32.0]);
        assert_eq!(s.quests[1].title, "Embers of the Sky Arch");
        assert_eq!(s.quests[1].giver, "Runekeeper Astrid");
        assert_eq!(s.quests[1].target, [40.0, 52.0, -10.0]);
        assert_eq!(s.quests[2].title, "Heart of the Crystal Deep");
        assert_eq!(s.quests[2].giver, "Hearth-Smith Torin");
        assert_eq!(s.quests[2].target, [0.0, -18.0, 0.0]);
        for q in &s.quests {
            assert!(!q.objectives.is_empty() && !q.lore.is_empty());
            assert!(q.reward_xp > 0 && q.reward_item.quantity > 0);
        }
    }

    #[test]
    fn quest_completion_grants_xp_levels_up_and_advances() {
        let dir = TempDir::new("quest");
        let store = RpgStore::open(&dir.0).unwrap();

        // 150 XP at level 1 (needs 100) -> level 2 with 50 carried over.
        let r = store.complete_quest("q_river_crossing").unwrap();
        println!(
            "after q1: level={} xp={}/{} hp={}/{} active={}",
            r.player.level,
            r.player.xp,
            r.player.xp_to_next,
            r.player.hp,
            r.player.max_hp,
            r.player.active_quest_id
        );
        assert_eq!(r.completion.xp_gained, 150);
        assert_eq!(r.completion.levels_gained, 1);
        assert_eq!(r.player.level, 2);
        assert_eq!(r.player.xp, 50);
        assert_eq!(r.player.xp_to_next, 150);
        assert_eq!(r.player.max_hp, 115.0);
        assert_eq!(r.player.hp, r.player.max_hp);
        assert_eq!(r.player.active_quest_id, "q_sky_arches");
        assert_eq!(r.player.completed_quests, ["q_river_crossing"]);
        assert!(r.player.inventory.iter().any(|i| i.id == "ferrymans_lantern"));
        assert_eq!(r.quests[0].status, "completed");
        assert_eq!(r.quests[1].status, "active");

        // Completing twice is rejected and changes nothing.
        let err = store.complete_quest("q_river_crossing").unwrap_err();
        assert_eq!(err.status_code(), 409);
        assert_eq!(store.snapshot().player.xp, 50);

        // Unknown quest -> 404.
        assert_eq!(store.complete_quest("q_nope").unwrap_err().status_code(), 404);

        // 50 + 300 = 350: level 2 needs 150 -> L3 (200 left), L3 needs 200 -> L4 (0).
        let r = store.complete_quest("q_sky_arches").unwrap();
        assert_eq!(r.completion.levels_gained, 2);
        assert_eq!(r.player.level, 4);
        assert_eq!(r.player.xp, 0);
        assert_eq!(r.player.xp_to_next, 250);
        assert_eq!(r.player.active_quest_id, "q_crystal_heart");

        // Final quest: weapon reward is auto-equipped; no quests remain.
        let r = store.complete_quest("q_crystal_heart").unwrap();
        assert_eq!(r.player.equipped_weapon, "Heartcrystal Blade");
        assert_eq!(r.player.active_quest_id, "");
        assert!(r.quests.iter().all(|q| q.status == "completed"));
        println!(
            "after all quests: level={} xp={}/{} inventory={:?}",
            r.player.level,
            r.player.xp,
            r.player.xp_to_next,
            r.player.inventory.iter().map(|i| &i.id).collect::<Vec<_>>()
        );
    }

    #[test]
    fn out_of_order_completion_keeps_earliest_open_quest_active() {
        let dir = TempDir::new("order");
        let store = RpgStore::open(&dir.0).unwrap();
        let r = store.complete_quest("q_crystal_heart").unwrap();
        assert_eq!(r.player.active_quest_id, "q_river_crossing");
        assert_eq!(r.quests[2].status, "completed");
    }

    #[test]
    fn structure_create_and_delete() {
        let dir = TempDir::new("structs");
        let store = RpgStore::open(&dir.0).unwrap();

        let a = store.add_structure(campfire(1.0)).unwrap();
        let b = store
            .add_structure(NewStructure {
                kind: "timber_wall".into(),
                position: [2.0, 4.0, 0.0],
                rotation_y: -std::f32::consts::FRAC_PI_2,
                seed: 7,
            })
            .unwrap();
        assert_eq!((a.id, b.id), (1, 2));
        assert_eq!(b.rotation_y, -std::f32::consts::FRAC_PI_2, "rotation stored verbatim");
        assert_eq!(store.snapshot().structures.len(), 2);

        let removed = store.remove_structure(a.id).unwrap();
        assert_eq!(removed, a);
        assert_eq!(store.remove_structure(a.id).unwrap_err().status_code(), 404);

        // Ids are monotonic and never reused after deletion.
        let c = store.add_structure(campfire(3.0)).unwrap();
        assert_eq!(c.id, 3);

        // Validation.
        for bad in [
            NewStructure { kind: "".into(), ..campfire(0.0) },
            NewStructure { kind: "Bad Kind!".into(), ..campfire(0.0) },
            NewStructure { position: [f32::NAN, 0.0, 0.0], ..campfire(0.0) },
            NewStructure { rotation_y: f32::INFINITY, ..campfire(0.0) },
        ] {
            assert_eq!(store.add_structure(bad).unwrap_err().status_code(), 400);
        }

        // Clear by seed only removes that seed's structures.
        assert_eq!(store.clear_structures(Some(7)).unwrap(), 1);
        assert_eq!(store.snapshot().structures, vec![c.clone()]);
        assert_eq!(store.clear_structures(None).unwrap(), 1);
        assert!(store.snapshot().structures.is_empty());
    }

    #[test]
    fn state_persists_across_reopen() {
        let dir = TempDir::new("persist");
        let expected = {
            let store = RpgStore::open(&dir.0).unwrap();
            store.complete_quest("q_river_crossing").unwrap();
            store.add_structure(campfire(1.0)).unwrap();
            let doomed = store.add_structure(campfire(2.0)).unwrap();
            store
                .add_structure(NewStructure {
                    kind: "torch_post".into(),
                    position: [5.5, 6.25, -1.0],
                    rotation_y: 1.25,
                    seed: 42,
                })
                .unwrap();
            store.remove_structure(doomed.id).unwrap();
            store.snapshot()
        };

        // No temp file left behind; the file is valid JSON.
        assert!(!dir.0.join("rpg_state.json.tmp").exists());
        let raw = fs::read_to_string(dir.0.join(RPG_STATE_FILE)).unwrap();
        let json: serde_json::Value = serde_json::from_str(&raw).unwrap();
        assert_eq!(json["next_structure_id"], 4);

        let reopened = RpgStore::open(&dir.0).unwrap();
        assert_eq!(reopened.snapshot(), expected);
        // Monotonic id counter survives the restart (deleted id 2 not reused).
        assert_eq!(reopened.add_structure(campfire(9.0)).unwrap().id, 4);
        // Completed quests survive the restart too.
        assert_eq!(
            reopened.complete_quest("q_river_crossing").unwrap_err().status_code(),
            409
        );
    }

    #[test]
    fn corrupt_file_is_moved_aside_and_reset() {
        let dir = TempDir::new("corrupt");
        fs::write(dir.0.join(RPG_STATE_FILE), b"{ not json").unwrap();
        let store = RpgStore::open(&dir.0).unwrap();
        assert_eq!(store.snapshot().player.level, 1);
        assert!(dir.0.join("rpg_state.json.corrupt").is_file());
    }

    #[test]
    fn sanitize_repairs_hand_edited_state() {
        let dir = TempDir::new("sanitize");
        // Stale id counter (1) behind an existing structure id (10).
        let mut s = PersistedState {
            next_structure_id: 1,
            ..Default::default()
        };
        s.structures.push(PlacedStructure {
            id: 10,
            kind: "timber_floor".into(),
            position: [0.0; 3],
            rotation_y: 0.0,
            seed: 42,
        });
        s.player.completed_quests = vec!["q_river_crossing".into(), "q_bogus".into()];
        s.player.active_quest_id = "q_river_crossing".into();
        fs::write(dir.0.join(RPG_STATE_FILE), serde_json::to_vec(&s).unwrap()).unwrap();

        let store = RpgStore::open(&dir.0).unwrap();
        let snap = store.snapshot();
        assert_eq!(snap.player.completed_quests, ["q_river_crossing"]);
        assert_eq!(snap.player.active_quest_id, "q_sky_arches");
        assert_eq!(store.add_structure(campfire(0.0)).unwrap().id, 11);
    }

    #[test]
    fn wire_json_is_flat_for_unity_json_utility() {
        let dir = TempDir::new("wire");
        let store = RpgStore::open(&dir.0).unwrap();
        store.add_structure(campfire(1.0)).unwrap();
        let v = serde_json::to_value(store.snapshot()).unwrap();

        // No nulls anywhere; arrays are plain arrays; objects have fixed keys.
        fn no_nulls(v: &serde_json::Value) -> bool {
            match v {
                serde_json::Value::Null => false,
                serde_json::Value::Array(a) => a.iter().all(no_nulls),
                serde_json::Value::Object(o) => o.values().all(no_nulls),
                _ => true,
            }
        }
        assert!(no_nulls(&v));
        assert!(v["player"]["inventory"].is_array());
        assert!(v["quests"][0]["target"].as_array().unwrap().len() == 3);
        assert!(v["structures"][0]["position"].as_array().unwrap().len() == 3);
        assert!(v["structures"][0]["id"].is_u64());
        println!("{}", serde_json::to_string_pretty(&v["structures"]).unwrap());
    }
}
