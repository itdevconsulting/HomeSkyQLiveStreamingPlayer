(() => {
  "use strict";

  const COMMAND_DELAY_MS = 250;
  const TV_GUIDE_STEPS = [
    { id: "sky_stream_home", waitAfterMs: COMMAND_DELAY_MS },
    { id: "sky_stream_down", waitAfterMs: COMMAND_DELAY_MS },
    { id: "sky_stream_down", waitAfterMs: COMMAND_DELAY_MS },
    { id: "sky_stream_ok", waitAfterMs: COMMAND_DELAY_MS },
    { id: "sky_stream_back", waitAfterMs: COMMAND_DELAY_MS },
    { id: "sky_stream_down", waitAfterMs: 0 }
  ];

  let sequenceBusy = false;

  function setStatus(message, type = "", holdMs = 1000) {
    const status = document.getElementById("sky-status");
    if (!status) return;

    status.textContent = message;
    status.className = `status ${type}`.trim();

    clearTimeout(setStatus.timer);
    if (holdMs > 0) {
      setStatus.timer = setTimeout(() => {
        status.textContent = "Ready";
        status.className = "status";
      }, holdMs);
    }
  }

  function delay(ms) {
    return new Promise(resolve => setTimeout(resolve, ms));
  }

  async function postCommand(id, element = null) {
    if (element) {
      element.classList.add("active");
      setTimeout(() => element.classList.remove("active"), 110);
    }

    const response = await fetch(`/button/${id}/press`, {
      method: "POST",
      body: "",
      cache: "no-store"
    });

    if (!response.ok) {
      throw new Error(`${id}: HTTP ${response.status}`);
    }
  }

  async function press(id, element = null) {
    if (sequenceBusy) {
      return;
    }

    try {
      await postCommand(id, element);
      setStatus(id.replace("sky_stream_", "").replaceAll("_", " "), "ok");
    } catch (error) {
      console.error("Sky Stream command failed:", error);
      setStatus(`Failed: ${id}`, "error");
    }
  }

  async function runSequence(name, steps, element = null) {
    if (sequenceBusy) {
      return;
    }

    sequenceBusy = true;
    if (element) {
      element.classList.add("active");
    }

    setStatus(name, "", 0);

    try {
      for (const step of steps) {
        const id = typeof step === "string" ? step : step.id;
        const waitAfterMs = typeof step === "string" ? COMMAND_DELAY_MS : step.waitAfterMs;
        setStatus(`${name}: ${id.replace("sky_stream_", "").replaceAll("_", " ")}`, "", 0);
        await postCommand(id);
        if (waitAfterMs > 0) {
          await delay(waitAfterMs);
        }
      }

      setStatus(name, "ok");
    } catch (error) {
      console.error(`${name} sequence failed:`, error);
      setStatus(`Failed: ${name}`, "error");
    } finally {
      sequenceBusy = false;
      if (element) {
        element.classList.remove("active");
      }
    }
  }

  async function tvGuide(element = null) {
    if (element) {
      element.classList.add("active");
    }

    try {
      sequenceBusy = true;
      setStatus("TV Guide", "", 0);
      await postCommand("sky_stream_tv_guide");
      const heldMs = TV_GUIDE_STEPS.reduce((sum, step) => sum + (step.waitAfterMs || 0), 0);
      if (heldMs > 0) {
        await delay(heldMs);
      }
      setStatus("TV Guide", "ok");
    } catch {
      sequenceBusy = false;
      await runSequence("TV Guide", TV_GUIDE_STEPS, element);
    } finally {
      if (element) {
        element.classList.remove("active");
      }

      sequenceBusy = false;
    }
  }

  
  function btn(id, html, classes = "remote-button round", title = "") {
    return `<button
      type="button"
      class="${classes}"
      data-command="${id}"
      aria-label="${title || id}"
      title="${title || ""}">
      ${html}
    </button>`;
  }

  function render() {
    document.title = "Sky Stream IR Remote";

    document.body.innerHTML = `
      <main id="sky-app">
        <section class="remote-shell" aria-label="Sky Stream remote control">

          <div class="device-head">
            <i class="device-led"></i>
            <span class="device-host">${location.hostname}</span>
          </div>

          <div class="remote-row three">
            ${btn("sky_stream_power", "⏻", "remote-button round icon", "Power / Standby")}
            ${btn("sky_stream_more", "•••", "remote-button round icon", "Options")}
            ${btn(
              "sky_stream_colour_button",
              '<span class="dots"><i class="dot red"></i><i class="dot yellow"></i><i class="dot green"></i><i class="dot blue"></i></span>',
              "remote-button round icon",
              "Coloured dots"
            )}
          </div>

          <div class="remote-row three" style="margin-top:14px">
            ${btn("sky_stream_back", "↶", "remote-button round icon back-correct", "Back")}
            ${btn(
              "sky_stream_play_pause",
              '<span class="playpause-css"><i class="tri"></i><span class="bars"><i></i><i></i></span></span>',
              "remote-button round icon",
              "Play / Pause"
            )}
            ${btn("sky_stream_mute", "🔇", "remote-button round icon", "Mute")}
          </div>

          <div class="dpad" aria-label="Navigation">
            <button type="button" class="dpad-up" data-command="sky_stream_up" aria-label="Up">⌃</button>
            <button type="button" class="dpad-left" data-command="sky_stream_left" aria-label="Left / Rewind">‹</button>
            <button type="button" class="dpad-ok" data-command="sky_stream_ok" aria-label="OK">OK</button>
            <button type="button" class="dpad-right" data-command="sky_stream_right" aria-label="Right / Fast forward">›</button>
            <button type="button" class="dpad-down" data-command="sky_stream_down" aria-label="Down">⌄</button>
          </div>

          <div class="remote-row lower">
            ${btn("sky_stream_plus", "+", "remote-button round icon plus", "Add to Playlist")}
            ${btn("sky_stream_home", "⌂", "remote-button round icon home", "Home")}

            <div class="remote-button vol" aria-label="Volume">
              <button type="button" class="vol-part" data-command="sky_stream_volume_up" aria-label="Volume up">+</button>
              <span class="vol-label">VOL</span>
              <button type="button" class="vol-part" data-command="sky_stream_volume_down" aria-label="Volume down">−</button>
            </div>
          </div>

          <div class="guide-row">
            <button
              type="button"
              id="tv-guide"
              class="remote-button guide-button"
              aria-label="TV Guide"
              title="TV Guide">
              <span class="guide-icon">▤</span>
              TV Guide
            </button>
          </div>

          <div class="keypad" aria-label="Number pad">
            ${[1,2,3,4,5,6,7,8,9]
              .map(n => `<button type="button" class="remote-button" data-command="sky_stream_${n}">${n}</button>`)
              .join("")}
            <button type="button" class="remote-button zero" data-command="sky_stream_0">0</button>
          </div>

          <div id="sky-status" class="status">Ready</div>
          <div class="footer">ESPHome • Sky Stream IR</div>
        </section>
      </main>
    `;

    document.querySelectorAll("[data-command]").forEach(element => {
      element.addEventListener("click", event => {
        event.preventDefault();
        press(element.dataset.command, element);
      });
    });

    document.getElementById("tv-guide").addEventListener("click", event => {
      event.preventDefault();
      tvGuide(event.currentTarget);
    });

    const keyMap = {
      ArrowUp: "sky_stream_up",
      ArrowDown: "sky_stream_down",
      ArrowLeft: "sky_stream_left",
      ArrowRight: "sky_stream_right",
      Home: "sky_stream_home",
      Enter: "sky_stream_ok",
      Escape: "sky_stream_back",
      Backspace: "sky_stream_back",
      " ": "sky_stream_play_pause",
      "+": "sky_stream_volume_up",
      "=": "sky_stream_volume_up",
      "-": "sky_stream_volume_down",
      "_": "sky_stream_volume_down",
      m: "sky_stream_mute",
      M: "sky_stream_mute",
      "0": "sky_stream_0",
      "1": "sky_stream_1",
      "2": "sky_stream_2",
      "3": "sky_stream_3",
      "4": "sky_stream_4",
      "5": "sky_stream_5",
      "6": "sky_stream_6",
      "7": "sky_stream_7",
      "8": "sky_stream_8",
      "9": "sky_stream_9"
    };

    document.addEventListener("keydown", event => {
      if (event.metaKey || event.ctrlKey || event.altKey || event.repeat) {
        return;
      }

      const id = keyMap[event.key];
      if (!id) {
        return;
      }

      event.preventDefault();

      const element = document.querySelector(`[data-command="${id}"]`);
      press(id, element);
    });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", render, { once: true });
  } else {
    render();
  }
})();
