using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Lives only in Bootstrap.unity. Waits one frame so every persistent
/// singleton (GameManager, SceneTransitionManager, AI agent) finishes Awake()
/// and registers itself, then loads the first real gameplay scene.
///
/// Bootstrap.unity should contain nothing except persistent systems and this
/// script — no visible content, per technical_architecture.md.
/// </summary>
public class BootstrapLoader : MonoBehaviour
{
    [Tooltip("The first scene to load once persistent systems are ready.")]
    [SerializeField] private string firstSceneName = "MountainTrail";

    [Tooltip("Extra delay in seconds before loading, in case any singleton needs more than one frame to warm up (e.g. RAG index build).")]
    [SerializeField] private float extraDelaySeconds = 0f;

    private IEnumerator Start()
    {
        // Let every other Awake()/Start() on persistent objects run first.
        yield return null;

        if (extraDelaySeconds > 0f)
            yield return new WaitForSeconds(extraDelaySeconds);

        Debug.Log($"[BootstrapLoader] Persistent systems ready — loading '{firstSceneName}'.");
        SceneManager.LoadScene(firstSceneName, LoadSceneMode.Single);
    }
}