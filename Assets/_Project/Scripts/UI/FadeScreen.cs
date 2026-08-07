using System.Collections;
using UnityEngine;

public class FadeScreen : MonoBehaviour
{
    public float fadeDuration = 1.0f;
    public Color fadeColor = Color.black;
    private Renderer rend;

    private void Awake()
    {
        gameObject.SetActive(true);
        rend = GetComponent<Renderer>();
        SetAlpha(0f);
    }

    public void StartFadeToClear()
    {
        gameObject.SetActive(true);
        StartCoroutine(FadeToClear());
    }

    public IEnumerator FadeToBlack()
    {
        gameObject.SetActive(true);
        rend = GetComponent<Renderer>();
        rend.material = new Material(rend.material);
        SetAlpha(0f);
        yield return FadeRoutine(0f, 1f);
    }

    public IEnumerator FadeToClear()
    {
        rend = GetComponent<Renderer>();
        SetAlpha(1f);
        yield return FadeRoutine(1f, 0f);
    }

    private IEnumerator FadeRoutine(float from, float to)
    {
        float timer = 0f;
        while (timer <= fadeDuration)
        {
            SetAlpha(Mathf.Lerp(from, to, timer / fadeDuration));
            timer += Time.deltaTime;
            yield return null;
        }
        SetAlpha(to);
    }

    private void SetAlpha(float alpha)
    {
        if (rend == null) rend = GetComponent<Renderer>();
        Color c = fadeColor;
        c.a = alpha;
        rend.material.SetColor("_BaseColor", c);
    }
}