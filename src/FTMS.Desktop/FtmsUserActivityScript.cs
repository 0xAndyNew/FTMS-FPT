namespace FTMS.Desktop;

internal static class FtmsUserActivityScript
{
    internal const string Value = """
        (() => {
          if (window !== window.top || window.__ftmsCompanionActivityInstalled) return;
          window.__ftmsCompanionActivityInstalled = true;
          let lastSent = 0;
          const report = force => {
            const now = Date.now();
            window.__ftmsCompanionLastInputAt = now;
            if (!force && now - lastSent < 400) return;
            lastSent = now;
            window.chrome?.webview?.postMessage('ftms-user-activity');
          };
          for (const eventName of ['pointerdown', 'keydown', 'input', 'touchstart'])
            document.addEventListener(eventName, () => report(true), { capture: true, passive: true });
          for (const eventName of ['wheel', 'scroll'])
            document.addEventListener(eventName, () => report(false), { capture: true, passive: true });
          window.addEventListener('focus', () => report(true));
        })();
        """;
}
