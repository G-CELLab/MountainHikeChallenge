using UnityEngine;

/// <summary>
/// Minimal lock that lets the tutor block interruptions while narration plays.
/// </summary>
public class NarrationLockController : MonoBehaviour
{
    public bool IsLocked { get; private set; }

    public void Lock()
    {
        IsLocked = true;
    }

    public void Unlock()
    {
        IsLocked = false;
    }
}