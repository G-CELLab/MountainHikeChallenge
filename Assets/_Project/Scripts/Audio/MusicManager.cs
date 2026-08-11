using System.Collections;
using UnityEngine;

/// <summary>
/// Persistent ambient music bed for the whole experience. Lives in Bootstrap.unity
/// next to GameManager/SceneTransitionManager (same DontDestroyOnLoad + duplicate-guard
/// pattern used everywhere else in the project).
///
/// Listens to GameManager.OnHikeStateChanged and crossfades between two looping
/// AudioSources whenever the state calls for a different track:
///   - Hiking / Transitioning / Summit  → trailMusic
///   - MiniGame                         → miniGameMusic
///
/// Two sources (not one) is what makes the crossfade smooth: the outgoing track
/// fades down on Source A while the incoming track fades up on Source B, then
/// they swap roles for next time. Nothing ever hard-cuts or restarts a track
/// that's already playing (e.g. Hiking → Transitioning keeps the same trail
/// music going instead of retriggering it).
///
/// Usage:
///   1. Drop this on a GameObject in Bootstrap.unity (e.g. "MusicManager").
///   2. Assign trailMusic and miniGameMusic clips in the Inspector.
///   The two playback AudioSources are created automatically in Awake and
///   are not Inspector-exposed — see CreateSource() if you need to route
///   them through an AudioMixerGroup later (e.g. for ducking under narration).
/// </summary>
public class MusicManager : MonoBehaviour
{
    public static MusicManager Instance { get; private set; }

    [Header("Tracks")]
    [Tooltip("Plays during Hiking and Transitioning. Also used for Summit unless summitMusic is set.")]
    public AudioClip trailMusic;

    [Tooltip("Plays during MiniGame (any organ-system interaction).")]
    public AudioClip miniGameMusic;

    [Tooltip("Optional — plays during Summit instead of trailMusic. Leave empty to just keep trailMusic going.")]
    public AudioClip summitMusic;

    [Header("Mix")]
    [Range(0f, 1f)] public float musicVolume = 0.5f;
    [Tooltip("Seconds for one track to fade out while the other fades in.")]
    public float crossfadeDuration = 2f;

    [Header("Debug")]
    public bool verbose = true;

    // Always auto-created in Awake — not Inspector-assignable, since hand-wiring
    // these would bypass the loop/volume/spatialBlend setup CreateSource() does.
    private AudioSource _sourceA;
    private AudioSource _sourceB;

    private AudioSource _activeSource;   // currently at (or fading to) full volume
    private AudioSource _inactiveSource; // silent, ready for the next track
    private AudioClip _currentClip;
    private Coroutine _fadeRoutine;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            if (verbose) Debug.Log("[MusicManager] Duplicate instance — destroying this one.");
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        _sourceA = CreateSource("MusicSource A");
        _sourceB = CreateSource("MusicSource B");

        _activeSource = _sourceA;
        _inactiveSource = _sourceB;
    }

    private AudioSource CreateSource(string label)
    {
        var go = new GameObject(label);
        go.transform.SetParent(transform);
        var src = go.AddComponent<AudioSource>();
        src.loop = true;
        src.playOnAwake = false;
        src.volume = 0f;
        src.spatialBlend = 0f; // 2D — ambient bed, not positional
        return src;
    }

    private IEnumerator Start()
    {
        // GameManager can initialize on the same frame or later depending on
        // scene/script execution order, so wait a beat before trusting Instance —
        // same defensive pattern SceneFlowController uses for AITutor.
        while (GameManager.Instance == null)
            yield return null;

        GameManager.Instance.OnHikeStateChanged += HandleHikeStateChanged;

        // SetHikeState() only fires the event when the state actually *changes*,
        // so on a fresh load we need to sync to whatever it already is manually.
        PlayForState(GameManager.Instance.CurrentHikeState, immediate: true);
    }

    private void OnDestroy()
    {
        if (GameManager.Instance != null)
            GameManager.Instance.OnHikeStateChanged -= HandleHikeStateChanged;
    }

    // ── State → track mapping ────────────────────────────────────────────────

    private void HandleHikeStateChanged(HikeState state) => PlayForState(state, immediate: false);

    private void PlayForState(HikeState state, bool immediate)
    {
        AudioClip target = state switch
        {
            HikeState.MiniGame => miniGameMusic,
            HikeState.Summit   => summitMusic != null ? summitMusic : trailMusic,
            _                  => trailMusic, // Hiking, Transitioning
        };

        PlayTrack(target, immediate);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Crossfades to clip. No-ops if clip is already the active track (so
    /// Hiking → Transitioning doesn't restart the trail music). Pass
    /// immediate = true to snap straight to full volume with no fade
    /// (used once on startup so the first track doesn't silently ramp up).
    /// </summary>
    public void PlayTrack(AudioClip clip, bool immediate = false)
    {
        if (clip == null)
        {
            if (verbose) Debug.LogWarning("[MusicManager] PlayTrack called with a null clip — ignoring.");
            return;
        }

        if (clip == _currentClip && _activeSource.isPlaying) return; // already the current track

        _currentClip = clip;

        if (_fadeRoutine != null) StopCoroutine(_fadeRoutine);

        if (immediate)
        {
            _activeSource.clip = clip;
            _activeSource.volume = musicVolume;
            _activeSource.Play();
            _inactiveSource.Stop();
            _inactiveSource.volume = 0f;
            if (verbose) Debug.Log($"[MusicManager] Starting '{clip.name}' immediately.");
            return;
        }

        _fadeRoutine = StartCoroutine(CrossfadeTo(clip));
    }

    /// <summary>Adjusts the target volume for whichever track is currently active/fading.</summary>
    public void SetVolume(float volume01)
    {
        musicVolume = Mathf.Clamp01(volume01);
        if (_fadeRoutine == null) _activeSource.volume = musicVolume;
    }

    // ── Crossfade ─────────────────────────────────────────────────────────────

    private IEnumerator CrossfadeTo(AudioClip clip)
    {
        if (verbose) Debug.Log($"[MusicManager] Crossfading → '{clip.name}' over {crossfadeDuration}s.");

        AudioSource fadeOut = _activeSource;
        AudioSource fadeIn = _inactiveSource;

        fadeIn.clip = clip;
        fadeIn.volume = 0f;
        fadeIn.Play();

        float t = 0f;
        float startOutVolume = fadeOut.volume;

        while (t < crossfadeDuration)
        {
            t += Time.deltaTime;
            float p = Mathf.Clamp01(t / crossfadeDuration);
            fadeOut.volume = Mathf.Lerp(startOutVolume, 0f, p);
            fadeIn.volume = Mathf.Lerp(0f, musicVolume, p);
            yield return null;
        }

        fadeOut.volume = 0f;
        fadeOut.Stop();
        fadeIn.volume = musicVolume;

        // Swap roles so next crossfade fades OUT the track that's now playing.
        _activeSource = fadeIn;
        _inactiveSource = fadeOut;

        _fadeRoutine = null;
    }
}