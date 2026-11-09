"use strict";

(() => {
  const key = location.hash.slice(1);
  const status = document.getElementById("status");
  let generation = 0;
  let timer;

  function unavailable(message) {
    document.documentElement.style.backgroundColor = "#202124";
    document.documentElement.style.color = "#ffffff";
    status.textContent = message;
    document.title = `Duane's Status - ${message}`;
  }

  function textColor(color) {
    const channels = color.match(/[0-9a-f]{2}/gi).map(value => {
      const channel = parseInt(value, 16) / 255;
      return channel <= 0.04045 ? channel / 12.92 : ((channel + 0.055) / 1.055) ** 2.4;
    });
    const luminance = channels[0] * 0.2126 + channels[1] * 0.7152 + channels[2] * 0.0722;
    return luminance > 0.179 ? "#000000" : "#ffffff";
  }

  async function refresh() {
    clearTimeout(timer);
    const current = ++generation;
    if (!/^[0-9A-F]{64}$/.test(key)) {
      unavailable("Link required");
      return;
    }
    const abort = new AbortController();
    const timeout = setTimeout(() => abort.abort(), 4000);
    try {
      const response = await fetch("/api/status", {
        headers: { "X-PresencePlus-Display-Key": key },
        cache: "no-store",
        credentials: "omit",
        signal: abort.signal
      });
      if (current !== generation) return;
      if (response.status === 401) {
        unavailable("Link expired");
        return;
      }
      if (!response.ok) throw new Error(`Display request failed (${response.status}).`);
      const value = await response.json();
      if (typeof value.name !== "string" || value.name.length === 0 ||
          typeof value.color !== "string" || !/^#[0-9a-f]{6}$/i.test(value.color)) {
        throw new Error("The PC returned an invalid status.");
      }
      if (current !== generation) return;
      document.documentElement.style.backgroundColor = value.color;
      document.documentElement.style.color = textColor(value.color);
      status.textContent = value.name;
      document.title = `Duane's Status - ${value.name}`;
    } catch (error) {
      if (current !== generation) return;
      unavailable("Disconnected");
    } finally {
      clearTimeout(timeout);
      if (current === generation) timer = setTimeout(refresh, 1000);
    }
  }

  window.addEventListener("offline", () => {
    ++generation;
    clearTimeout(timer);
    unavailable("Disconnected");
  });
  window.addEventListener("online", refresh);
  document.addEventListener("visibilitychange", () => {
    if (!document.hidden) {
      unavailable("Connecting...");
      refresh();
    }
  });
  refresh();
})();
