(() => {
  "use strict";

  const STORAGE_KEY = "sky-stream-remote-v1";
  const MAX_MACROS = 12;
  const MAX_MACRO_STEPS = 40;
  const KEY_OPTIONS = [
    ["sky_stream_home", "Home"],
    ["sky_stream_back", "Back"],
    ["sky_stream_ok", "OK"],
    ["sky_stream_up", "Up"],
    ["sky_stream_down", "Down"],
    ["sky_stream_left", "Left"],
    ["sky_stream_right", "Right"],
    ["sky_stream_1", "1"],
    ["sky_stream_2", "2"],
    ["sky_stream_3", "3"],
    ["sky_stream_4", "4"],
    ["sky_stream_5", "5"],
    ["sky_stream_6", "6"],
    ["sky_stream_7", "7"],
    ["sky_stream_8", "8"],
    ["sky_stream_9", "9"],
    ["sky_stream_0", "0"],
    ["sky_stream_play_pause", "Play/Pause"],
    ["sky_stream_plus", "Plus"],
    ["sky_stream_more", "More"],
    ["sky_stream_colour_button", "Colour"],
    ["sky_stream_mute", "Mute"],
    ["sky_stream_volume_up", "Vol +"],
    ["sky_stream_volume_down", "Vol −"],
    ["sky_stream_power", "Power"]
  ];
  const DEFAULT_WAITS = {
    afterHome: 5000,
    afterFirstDown: 3000,
    afterSecondDown: 2000,
    afterOk: 2000,
    afterBack: 1000,
    afterLastDown: 5000,
    betweenDigits: 600,
    beforeTuneOk: 4000,
    betweenTuneOks: 1000
  };
  const WAIT_FIELDS = [
    { key: "afterHome", label: "After Home" },
    { key: "afterFirstDown", label: "After first Down" },
    { key: "afterSecondDown", label: "After second Down" },
    { key: "afterOk", label: "After Guide OK" },
    { key: "afterBack", label: "After Back" },
    { key: "afterLastDown", label: "After last Down" },
    { key: "betweenDigits", label: "Between digits" },
    { key: "beforeTuneOk", label: "After number, before OK" },
    { key: "betweenTuneOks", label: "Between Live TV OKs" }
  ];

  function key(id, label) {
    return { type: "press", id, label };
  }

  function wait(ms) {
    return { type: "delay", ms };
  }

  function clampWaitMs(value, fallback) {
    const number = Number(value);
    if (!Number.isFinite(number)) {
      return fallback;
    }
    return Math.max(0, Math.min(30000, Math.round(number)));
  }

  function newMacroId() {
    return "m" + Math.random().toString(36).slice(2, 8) + Date.now().toString(36).slice(-3);
  }

  function normalizeStep(step) {
    if (!step || typeof step !== "object") {
      return null;
    }
    if (step.type === "press" && KEY_OPTIONS.some(option => option[0] === step.id)) {
      return { type: "press", id: step.id };
    }
    if (step.type === "delay") {
      return { type: "delay", ms: clampWaitMs(step.ms, 1000) };
    }
    if (step.type === "guide") {
      return { type: "guide" };
    }
    if (step.type === "macro" && step.macroId) {
      return { type: "macro", macroId: String(step.macroId) };
    }
    return null;
  }

  function normalizeMacro(raw) {
    if (!raw || typeof raw !== "object") {
      return null;
    }
    const name = String(raw.name || "").trim().slice(0, 24);
    if (!name) {
      return null;
    }
    const steps = Array.isArray(raw.steps)
      ? raw.steps.map(normalizeStep).filter(Boolean).slice(0, MAX_MACRO_STEPS)
      : [];
    return {
      id: String(raw.id || newMacroId()),
      name: name,
      steps: steps
    };
  }

  function loadConfig() {
    const config = {
      waits: Object.assign({}, DEFAULT_WAITS),
      macros: []
    };
    try {
      const raw = window.localStorage.getItem(STORAGE_KEY);
      if (!raw) {
        return config;
      }
      const parsed = JSON.parse(raw);
      const stored = parsed && parsed.waits ? parsed.waits : parsed;
      Object.keys(DEFAULT_WAITS).forEach(name => {
        if (stored && stored[name] != null) {
          config.waits[name] = clampWaitMs(stored[name], DEFAULT_WAITS[name]);
        }
      });
      if (parsed && Array.isArray(parsed.macros)) {
        config.macros = parsed.macros.map(normalizeMacro).filter(Boolean).slice(0, MAX_MACROS);
      }
      return config;
    } catch (error) {
      return config;
    }
  }

  function persistConfig() {
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify({
      v: 1,
      waits: waits,
      macros: macros
    }));
  }

  function blankEditor() {
    return { id: null, name: "", steps: [] };
  }

  function guideSteps(currentWaits) {
    return [
      key("sky_stream_home"),
      wait(currentWaits.afterHome),
      key("sky_stream_down"),
      wait(currentWaits.afterFirstDown),
      key("sky_stream_down"),
      wait(currentWaits.afterSecondDown),
      key("sky_stream_ok"),
      wait(currentWaits.afterOk),
      key("sky_stream_back"),
      wait(currentWaits.afterBack),
      key("sky_stream_down"),
      wait(currentWaits.afterLastDown)
    ];
  }

  function expandSteps(steps, seen) {
    const out = [];
    (steps || []).forEach(step => {
      if (step.type === "guide") {
        out.push.apply(out, guideSteps(waits));
        return;
      }
      if (step.type === "macro") {
        if (seen.has(step.macroId)) {
          throw new Error("Macro loop: " + step.macroId);
        }
        const nested = macros.find(item => item.id === step.macroId);
        if (!nested) {
          throw new Error("Missing macro");
        }
        const nextSeen = new Set(seen);
        nextSeen.add(step.macroId);
        out.push.apply(out, expandSteps(nested.steps, nextSeen));
        return;
      }
      out.push(step);
    });
    return out;
  }

  function runMacro(id) {
    const macro = macros.find(item => item.id === id);
    if (!macro) {
      return;
    }
    try {
      const steps = expandSteps(macro.steps, new Set([macro.id]));
      if (!steps.length) {
        setStatus("Macro is empty", "error", 4000);
        return;
      }
      return runSequence(steps, macro.name);
    } catch (error) {
      setStatus(error.message || "Macro failed", "error", 6000);
    }
  }

  function draftStepLabel(step) {
    if (step.type === "delay") {
      return "Wait " + (step.ms / 1000) + "s";
    }
    if (step.type === "guide") {
      return "TV Guide";
    }
    if (step.type === "macro") {
      const nested = macros.find(item => item.id === step.macroId);
      return "Macro · " + (nested ? nested.name : "?");
    }
    return prettyLabel(step.id);
  }

  const loaded = loadConfig();
  let waits = loaded.waits;
  let macros = loaded.macros;
  let editor = blankEditor();
  const TV_GUIDE_FOOTER = "Locked during sequences";
  const CHANNELS = [
    [101, "BBC One", "Entertainment"],
    [102, "BBC Two", "Entertainment"],
    [103, "ITV1", "Entertainment"],
    [104, "Channel 4", "Entertainment"],
    [105, "Channel 5", "Entertainment"],
    [106, "Sky One", "Entertainment"],
    [107, "Sky Witness", "Entertainment"],
    [108, "Sky Atlantic", "Entertainment"],
    [109, "Sky Comedy", "Entertainment"],
    [110, "Sky Documentaries", "Entertainment"],
    [111, "Sky Crime", "Entertainment"],
    [112, "Sky Arts", "Entertainment"],
    [113, "Sky Nature", "Entertainment"],
    [114, "Sky Sci-Fi", "Entertainment"],
    [115, "Sky History", "Entertainment"],
    [118, "BBC Three", "Entertainment"],
    [119, "BBC Four", "Entertainment"],
    [120, "U&Alibi", "Entertainment"],
    [121, "U&Gold", "Entertainment"],
    [122, "ITV2", "Entertainment"],
    [123, "ITV3", "Entertainment"],
    [124, "ITV4", "Entertainment"],
    [125, "ITV Quiz", "Entertainment"],
    [126, "E4", "Entertainment"],
    [127, "More4", "Entertainment"],
    [128, "5STAR", "Entertainment"],
    [129, "5USA", "Entertainment"],
    [130, "U&Dave", "Entertainment"],
    [131, "U&W", "Entertainment"],
    [132, "U&Drama", "Entertainment"],
    [133, "U&Yesterday", "Entertainment"],
    [134, "U&Eden", "Entertainment"],
    [135, "Comedy Central", "Entertainment"],
    [136, "Comedy Central Extra", "Entertainment"],
    [137, "MTV", "Entertainment"],
    [138, "Discovery", "Entertainment"],
    [139, "TLC", "Entertainment"],
    [140, "Investigation Discovery", "Entertainment"],
    [141, "Animal Planet", "Entertainment"],
    [142, "Crime + Investigation", "Entertainment"],
    [143, "Sky History 2", "Entertainment"],
    [144, "National Geographic", "Entertainment"],
    [145, "Nat Geo Wild", "Entertainment"],
    [146, "Discovery Turbo", "Entertainment"],
    [147, "Discovery History", "Entertainment"],
    [148, "Discovery Science", "Entertainment"],
    [149, "Quest", "Entertainment"],
    [150, "Quest Red", "Entertainment"],
    [151, "DMAX", "Entertainment"],
    [152, "Food Network", "Entertainment"],
    [153, "Really", "Entertainment"],
    [155, "True Crime", "Entertainment"],
    [156, "Legend", "Entertainment"],
    [157, "True Crime Xtra", "Entertainment"],
    [158, "Sky Mix", "Entertainment"],
    [159, "Challenge", "Entertainment"],
    [163, "4seven", "Entertainment"],
    [164, "E4 Extra", "Entertainment"],
    [165, "5ACTION", "Entertainment"],
    [166, "5SELECT", "Entertainment"],
    [167, "GREAT! TV", "Entertainment"],
    [169, "Blaze", "Entertainment"],
    [170, "PBS America", "Entertainment"],
    [171, "Together TV", "Entertainment"],
    [172, "S4C", "Entertainment"],
    [173, "BBC Scotland", "Entertainment"],
    [174, "BBC Alba", "Entertainment"],
    [201, "CBBC", "Kids"],
    [202, "CBeebies", "Kids"],
    [203, "Sky Kids", "Kids"],
    [204, "Disney Jr", "Kids"],
    [205, "Nickelodeon", "Kids"],
    [206, "Nicktoons", "Kids"],
    [207, "Nick Jr", "Kids"],
    [208, "Nick Jr Too", "Kids"],
    [209, "Cartoon Network", "Kids"],
    [210, "Boomerang", "Kids"],
    [211, "Cartoonito", "Kids"],
    [212, "BabyTV", "Kids"],
    [301, "Sky Cinema Premiere", "Movies"],
    [302, "Sky Cinema Animation", "Movies"],
    [303, "Sky Cinema Box Set", "Movies"],
    [304, "Sky Cinema Family", "Movies"],
    [305, "Disney+ Cinema", "Movies"],
    [306, "Sky Cinema Action", "Movies"],
    [307, "Sky Cinema Greats", "Movies"],
    [308, "Sky Cinema Comedy", "Movies"],
    [309, "Sky Cinema Thriller", "Movies"],
    [310, "Sky Cinema Drama", "Movies"],
    [311, "Sky Cinema Sci-Fi/Horror", "Movies"],
    [312, "Film4", "Movies"],
    [313, "Movies24", "Movies"],
    [314, "Movies24+", "Movies"],
    [316, "Legend Xtra", "Movies"],
    [317, "GREAT! Action", "Movies"],
    [318, "GREAT! Mystery", "Movies"],
    [319, "GREAT! Romance", "Movies"],
    [354, "Clubland TV", "Music"],
    [355, "NOW 70s", "Music"],
    [356, "NOW 80s", "Music"],
    [357, "NOW 90s & 00s", "Music"],
    [358, "NOW Rock", "Music"],
    [401, "Sky Sports Main Event", "Sports"],
    [402, "Sky Sports Premier League", "Sports"],
    [403, "Sky Sports Football", "Sports"],
    [404, "Sky Sports+", "Sports"],
    [405, "Sky Sports Cricket", "Sports"],
    [406, "Sky Sports Golf", "Sports"],
    [407, "Sky Sports F1", "Sports"],
    [408, "Sky Sports Tennis", "Sports"],
    [409, "Sky Sports News", "Sports"],
    [410, "Sky Sports Action", "Sports"],
    [411, "Sky Sports Racing", "Sports"],
    [412, "Sky Sports Mix", "Sports"],
    [413, "TNT Sports 1", "Sports"],
    [414, "TNT Sports 2", "Sports"],
    [415, "TNT Sports 3", "Sports"],
    [416, "TNT Sports 4", "Sports"],
    [417, "GINX eSports", "Sports"],
    [418, "MUTV", "Sports"],
    [419, "LFCTV", "Sports"],
    [493, "TNT Sports Ultimate", "Sports"],
    [501, "Sky News", "News"],
    [502, "BBC News", "News"],
    [503, "BBC Parliament", "News"],
    [504, "CNBC", "News"],
    [505, "Bloomberg", "News"],
    [506, "CNN", "News"],
    [508, "TalkTV", "News"],
    [509, "GB News", "News"],
    [510, "Euronews", "News"],
    [511, "NDTV 24x7", "News"],
    [512, "France 24", "News"],
    [601, "QVC", "Shopping"],
    [602, "QVC Style", "Shopping"],
    [603, "QVC Beauty", "Shopping"],
    [604, "QVC Extra", "Shopping"],
    [701, "Star Bharat", "International"],
    [702, "Star Plus", "International"],
    [703, "Star Gold", "International"],
    [704, "Sony TV", "International"],
    [705, "Sony MAX", "International"],
    [706, "Sony SAB", "International"],
    [707, "Sony MAX 2", "International"],
    [708, "Colors", "International"],
    [709, "Colors Rishtey", "International"],
    [710, "Colors Gujarati", "International"],
    [711, "B4U Movies", "International"],
    [712, "B4U Music", "International"],
    [713, "Zee Cinema", "International"],
    [714, "Zee TV", "International"],
    [716, "Sky News Arabia", "International"],
    [720, "HUM News", "International"],
    [721, "HUM Europe", "International"],
    [722, "Geo News", "International"],
    [723, "Geo TV", "International"],
    [724, "ARY Digital", "International"],
    [725, "New Vision", "International"],
    [740, "PTC Punjabi", "International"],
    [741, "Zee Punjabi", "International"],
    [742, "Brit Asia", "International"]
  ];


  let sequenceBusy = false;

  function injectStyles() {
    if (document.getElementById("sky-remote-css")) {
      return;
    }

    const style = document.createElement("style");
    style.id = "sky-remote-css";
    style.textContent = ':root {\n  color-scheme: dark;\n  --page: #071019;\n  --remote: #161b20;\n  --remote-edge: #050709;\n  --button: #20262c;\n  --button-low: #12171b;\n  --text: #f5f7f9;\n  --muted: #8d98a3;\n  --green: #39e56d;\n  --red: #ff2c50;\n  --blue: #178cff;\n  --yellow: #ffd12f;\n}\n\n* {\n  box-sizing: border-box;\n  -webkit-tap-highlight-color: transparent;\n}\n\nhtml, body {\n  margin: 0;\n  min-height: 100%;\n  background:\n    radial-gradient(circle at 50% -15%, #203143 0%, #0c1823 37%, var(--page) 76%);\n  color: var(--text);\n  font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;\n}\n\nbody {\n  min-height: 100vh;\n  display: flex;\n  justify-content: center;\n  padding: 16px 8px 28px;\n}\n\n#sky-app {\n  width: min(100%, 320px);\n}\n\n.remote-shell {\n  position: relative;\n  width: 100%;\n  padding: 24px 20px 25px;\n  border: 2px solid var(--remote-edge);\n  border-radius: 42px 42px 72px 72px / 36px 36px 58px 58px;\n  background:\n    linear-gradient(155deg, rgba(255,255,255,.045), transparent 24%),\n    linear-gradient(180deg, #1b2025 0%, #14191e 53%, #0f1418 100%);\n  box-shadow:\n    0 28px 65px rgba(0,0,0,.58),\n    inset 0 1px 1px rgba(255,255,255,.09),\n    inset 0 -2px 5px rgba(0,0,0,.48);\n  user-select: none;\n  overflow: hidden;\n}\n\n.remote-shell::after {\n  content: "";\n  position: absolute;\n  inset: 3px;\n  pointer-events: none;\n  border-radius: inherit;\n  border: 1px solid rgba(255,255,255,.035);\n}\n\n.device-head {\n  display: flex;\n  align-items: center;\n  justify-content: center;\n  gap: 9px;\n  margin: 0 0 20px;\n  min-height: 24px;\n}\n\n.device-led {\n  width: 8px;\n  height: 8px;\n  border-radius: 50%;\n  background: var(--green);\n  box-shadow: 0 0 8px rgba(57,229,109,.65);\n}\n\n.device-host {\n  font-size: 15px;\n  font-weight: 750;\n  letter-spacing: .05px;\n}\n\n.remote-row {\n  display: grid;\n  grid-template-columns: repeat(3, 1fr);\n  align-items: center;\n  gap: 15px;\n  margin-bottom: 14px;\n}\n\n.remote-row.lower {\n  margin: 3px 0 17px;\n}\n\n.remote-button {\n  appearance: none;\n  display: inline-flex;\n  align-items: center;\n  justify-content: center;\n  border: 2px solid #050708;\n  color: var(--text);\n  background: linear-gradient(150deg, var(--button), var(--button-low));\n  box-shadow:\n    inset 0 1px 1px rgba(255,255,255,.11),\n    0 4px 8px rgba(0,0,0,.38);\n  cursor: pointer;\n  touch-action: manipulation;\n  transition: transform 60ms ease, filter 60ms ease;\n}\n\n.remote-button:active,\n.remote-button.active {\n  transform: translateY(2px) scale(.955);\n  filter: brightness(1.28);\n}\n\n.remote-button.round {\n  width: 61px;\n  height: 61px;\n  border-radius: 50%;\n  justify-self: center;\n}\n\n.remote-button.icon {\n  font-size: 22px;\n  line-height: 1;\n}\n\n.dots {\n  display: flex;\n  gap: 3px;\n}\n\n.dot {\n  width: 7px;\n  height: 7px;\n  border-radius: 50%;\n}\n\n.dot.red { background: var(--red); }\n.dot.yellow { background: var(--yellow); }\n.dot.green { background: var(--green); }\n.dot.blue { background: var(--blue); }\n\n.playpause-css {\n  display: flex;\n  align-items: center;\n  justify-content: center;\n  gap: 3px;\n}\n\n.playpause-css .tri {\n  width: 0;\n  height: 0;\n  border-style: solid;\n  border-width: 7px 0 7px 11px;\n  border-color: transparent transparent transparent currentColor;\n}\n\n.playpause-css .bars {\n  display: flex;\n  gap: 3px;\n}\n\n.playpause-css .bars i {\n  display: block;\n  width: 3px;\n  height: 14px;\n  background: currentColor;\n  border-radius: 1px;\n}\n\n.dpad {\n  position: relative;\n  width: 225px;\n  height: 225px;\n  margin: 17px auto 18px;\n  border: 2px solid #050708;\n  border-radius: 50%;\n  background: linear-gradient(145deg, #1d2328, #101519);\n  box-shadow:\n    inset 0 1px 1px rgba(255,255,255,.075),\n    0 6px 14px rgba(0,0,0,.33);\n}\n\n.dpad > button {\n  position: absolute;\n  border: 0;\n  background: transparent;\n  color: #fff;\n  cursor: pointer;\n  touch-action: manipulation;\n  padding: 0;\n  font-size: 28px;\n  line-height: 1;\n}\n\n.dpad > button:active,\n.dpad > button.active {\n  filter: brightness(1.45);\n}\n\n.dpad-up    { top: 5px; left: 66px; width: 90px; height: 62px; }\n.dpad-down  { bottom: 5px; left: 66px; width: 90px; height: 62px; }\n.dpad-left  { left: 5px; top: 66px; width: 62px; height: 90px; }\n.dpad-right { right: 5px; top: 66px; width: 62px; height: 90px; }\n\n.dpad-ok {\n  left: 50%;\n  top: 50%;\n  width: 91px !important;\n  height: 91px !important;\n  transform: translate(-50%, -50%);\n  border-radius: 50% !important;\n  border: 2px solid #050708 !important;\n  background: linear-gradient(150deg, #20262c, #11161a) !important;\n  color: white !important;\n  font-size: 19px;\n  font-weight: 800;\n  box-shadow: inset 0 1px 1px rgba(255,255,255,.09);\n}\n\n.dpad-ok:active,\n.dpad-ok.active {\n  transform: translate(-50%, -48%) scale(.96);\n}\n\n.plus {\n  border-color: var(--red);\n}\n\n.home {\n  border-color: var(--green);\n}\n\n.plus {\n  font-size: 29px;\n  font-weight: 500;\n}\n\n.remote-button.vol {\n  justify-self: center;\n  width: 60px;\n  height: 132px;\n  border-radius: 29px;\n  padding: 0;\n  overflow: hidden;\n  display: grid;\n  grid-template-rows: 1fr auto 1fr;\n  box-shadow:\n    inset 0 1px 1px rgba(255,255,255,.1),\n    0 4px 8px rgba(0,0,0,.38);\n}\n\n.remote-button.vol:active,\n.remote-button.vol.active {\n  transform: none;\n  filter: none;\n}\n\n.vol-part {\n  border: 0;\n  background: transparent;\n  color: white;\n  font-size: 28px;\n  font-weight: 400;\n  cursor: pointer;\n  touch-action: manipulation;\n}\n\n.vol-part:active,\n.vol-part.active {\n  background: rgba(255,255,255,.08);\n}\n\n.vol-label {\n  color: #b6c0ca;\n  text-align: center;\n  font-size: 11px;\n  letter-spacing: .7px;\n}\n\n.guide-row {\n  display: flex;\n  justify-content: center;\n  margin: 14px 0 17px;\n}\n\n.guide-button {\n  width: 170px;\n  height: 46px;\n  border-radius: 23px;\n  font-size: 14px;\n  font-weight: 750;\n  letter-spacing: .2px;\n  gap: 8px;\n}\n\n.guide-button .guide-icon {\n  font-size: 18px;\n}\n\n.keypad {\n  display: grid;\n  grid-template-columns: repeat(3, 61px);\n  justify-content: center;\n  gap: 11px 20px;\n}\n\n.keypad .remote-button {\n  width: 61px;\n  height: 49px;\n  border-radius: 25px;\n  font-size: 17px;\n  font-weight: 750;\n}\n\n.keypad .zero {\n  grid-column: 2;\n}\n\n.status {\n  min-height: 17px;\n  margin-top: 17px;\n  text-align: center;\n  color: var(--muted);\n  font-size: 10px;\n}\n\n.status.ok { color: #69e683; }\n.status.error { color: #ff7186; }\n\n.footer {\n  margin-top: 6px;\n  text-align: center;\n  color: #66717b;\n  font-size: 9px;\n}\n\n@media (max-width: 330px) {\n  body { padding-left: 3px; padding-right: 3px; }\n  #sky-app { width: 310px; }\n  .remote-shell { padding-left: 16px; padding-right: 16px; }\n}\n';
    document.head.appendChild(style);
  }

  function injectLiveTvStyles() {
    if (document.getElementById("sky-live-tv-css")) {
      return;
    }

    const style = document.createElement("style");
    style.id = "sky-live-tv-css";
    style.textContent = `
.live-tv {
  display: flex;
  flex-direction: column;
  gap: 8px;
  width: 226px;
  margin: 0 auto 14px;
}
.live-tv-label {
  text-align: center;
  font-size: 11px;
  font-weight: 750;
  letter-spacing: .4px;
  color: var(--muted);
}
.live-tv-search,
.live-tv-select {
  appearance: none;
  width: 100%;
  border: 2px solid #050708;
  border-radius: 14px;
  background: linear-gradient(150deg, var(--button), var(--button-low));
  color: var(--text);
  font: 650 13px/1.3 -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
  padding: 10px 12px;
  box-shadow:
    inset 0 1px 1px rgba(255,255,255,.11),
    0 4px 8px rgba(0,0,0,.38);
}
.live-tv-search::placeholder {
  color: var(--muted);
  font-weight: 500;
}
.live-tv-search:focus,
.live-tv-select:focus {
  outline: 1px solid rgba(23,140,255,.55);
}
.live-tv-select {
  cursor: pointer;
}
.live-tv-search:disabled,
.live-tv-select:disabled {
  opacity: .55;
  cursor: not-allowed;
}
.live-tv-select option,
.live-tv-select optgroup {
  background: #161b20;
  color: var(--text);
}
.sequence-hud {
  margin: 0 0 16px;
  padding: 12px 12px 11px;
  border: 2px solid #050708;
  border-radius: 18px;
  background:
    linear-gradient(180deg, rgba(57,229,109,.08), transparent 42%),
    linear-gradient(180deg, #1c2833, #12181d);
  box-shadow:
    inset 0 1px 1px rgba(255,255,255,.08),
    0 6px 14px rgba(0,0,0,.28);
}
.sequence-labels {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 10px;
  margin-bottom: 10px;
}
.sequence-kicker {
  display: block;
  font-size: 9px;
  letter-spacing: .8px;
  text-transform: uppercase;
  color: var(--muted);
  margin-bottom: 3px;
}
.sequence-labels strong {
  display: block;
  font-size: 15px;
  font-weight: 800;
  letter-spacing: .2px;
  text-transform: capitalize;
  line-height: 1.15;
}
#sequence-now { color: var(--green); }
#sequence-next { color: #9be7ff; }
.sequence-track {
  height: 9px;
  border-radius: 99px;
  background: #0b1014;
  border: 1px solid #050708;
  overflow: hidden;
}
.sequence-bar {
  display: block;
  width: 100%;
  height: 100%;
  transform: scaleX(1);
  transform-origin: left center;
  border-radius: inherit;
  background: linear-gradient(90deg, #39e56d, #178cff);
  box-shadow: 0 0 12px rgba(57,229,109,.4);
  transition: none;
}
.sequence-caption {
  margin-top: 8px;
  text-align: center;
  font-size: 10px;
  color: var(--muted);
}
.remote-shell.is-locked {
  cursor: wait;
}
.remote-shell.is-locked .device-led {
  background: var(--yellow);
  box-shadow: 0 0 8px rgba(255,209,47,.7);
}
.remote-shell.is-locked button,
.remote-shell.is-locked input,
.remote-shell.is-locked select {
  pointer-events: none !important;
  opacity: .42;
}
.remote-shell.is-locked button.active {
  opacity: 1;
}
.device-head {
  justify-content: space-between;
}
.device-title {
  display: flex;
  align-items: center;
  gap: 9px;
  min-width: 0;
}
.setup-toggle {
  appearance: none;
  border: 2px solid #050708;
  border-radius: 12px;
  background: linear-gradient(150deg, var(--button), var(--button-low));
  color: var(--muted);
  font: 750 11px/1 -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
  letter-spacing: .3px;
  padding: 7px 10px;
  cursor: pointer;
  box-shadow:
    inset 0 1px 1px rgba(255,255,255,.11),
    0 4px 8px rgba(0,0,0,.38);
}
.setup-toggle[aria-expanded="true"] {
  color: var(--text);
}
.setup-panel {
  margin-top: 14px;
  padding: 12px;
  border: 2px solid #050708;
  border-radius: 18px;
  background: linear-gradient(180deg, #1c2833, #12181d);
}
.setup-hint {
  margin: 0 0 10px;
  color: var(--muted);
  font-size: 10px;
  line-height: 1.35;
  text-align: center;
}
.setup-row {
  display: grid;
  grid-template-columns: 1fr 64px;
  align-items: center;
  gap: 8px;
  margin-bottom: 7px;
}
.setup-row label {
  color: var(--text);
  font-size: 11px;
  font-weight: 650;
}
.setup-row input {
  width: 100%;
  border: 2px solid #050708;
  border-radius: 10px;
  background: #0b1014;
  color: var(--text);
  font: 700 13px/1.2 -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
  padding: 7px 6px;
  text-align: center;
}
.setup-actions {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 8px;
  margin-top: 12px;
}
.setup-actions button {
  appearance: none;
  height: 36px;
  border: 2px solid #050708;
  border-radius: 12px;
  background: linear-gradient(150deg, var(--button), var(--button-low));
  color: var(--text);
  font: 750 12px/1 -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
  cursor: pointer;
}
#sky-app {
  width: min(100%, 454px);
  display: flex;
  align-items: flex-start;
  justify-content: center;
  gap: 10px;
}
.remote-shell {
  width: 320px;
  max-width: 100%;
  flex: 0 1 320px;
}
.macro-rail {
  display: flex;
  flex-direction: column;
  gap: 8px;
  flex: 0 0 114px;
  width: 114px;
  padding-top: 18px;
}
.macro-empty {
  color: var(--muted);
  font-size: 10px;
  line-height: 1.35;
  text-align: center;
}
.macro-chip {
  appearance: none;
  width: 100%;
  min-height: 42px;
  padding: 8px 8px;
  border: 2px solid #050708;
  border-radius: 14px;
  background: linear-gradient(150deg, var(--button), var(--button-low));
  color: var(--text);
  font: 750 11px/1.2 -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
  cursor: pointer;
  word-break: break-word;
  box-shadow:
    inset 0 1px 1px rgba(255,255,255,.11),
    0 4px 8px rgba(0,0,0,.38);
}
#sky-app.is-locked {
  cursor: wait;
}
#sky-app.is-locked .macro-rail button {
  pointer-events: none !important;
  opacity: .42;
}
.setup-heading {
  margin: 16px 0 8px;
  color: var(--text);
  font-size: 12px;
  font-weight: 800;
  letter-spacing: .4px;
  text-align: center;
  text-transform: uppercase;
}
.macro-saved {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-bottom: 10px;
}
.macro-saved-row {
  display: grid;
  grid-template-columns: 1fr auto auto;
  gap: 6px;
  align-items: center;
}
.macro-saved-row span {
  font-size: 12px;
  font-weight: 700;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.macro-mini {
  appearance: none;
  height: 28px;
  padding: 0 8px;
  border: 2px solid #050708;
  border-radius: 10px;
  background: #0b1014;
  color: var(--text);
  font: 750 11px/1 sans-serif;
  cursor: pointer;
}
#macro-name,
#macro-add-wait,
#macro-add-key,
#macro-add-macro {
  width: 100%;
  border: 2px solid #050708;
  border-radius: 10px;
  background: #0b1014;
  color: var(--text);
  font: 650 13px/1.2 -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
  padding: 8px 8px;
  margin-bottom: 7px;
}
.macro-step {
  display: grid;
  grid-template-columns: 1fr auto auto auto;
  gap: 4px;
  align-items: center;
  margin-bottom: 5px;
  font-size: 11px;
  font-weight: 650;
}
.macro-add-row {
  display: grid;
  grid-template-columns: 1fr auto;
  gap: 6px;
  margin-bottom: 7px;
}
@media (max-width: 470px) {
  #sky-app {
    width: min(100%, 320px);
    flex-direction: column;
    align-items: stretch;
  }
  .macro-rail {
    flex-direction: row;
    flex-wrap: wrap;
    width: 100%;
    flex-basis: auto;
    padding-top: 0;
  }
  .macro-chip {
    flex: 1 1 96px;
    width: auto;
  }
}
`;
    document.head.appendChild(style);
  }


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

  function prettyLabel(id) {
    return String(id).replace("sky_stream_", "").split("_").join(" ");
  }

  function sleep(ms) {
    return new Promise(resolve => window.setTimeout(resolve, ms));
  }

  function postCommand(id, element = null, signal = undefined) {
    if (element) {
      element.classList.add("active");
      setTimeout(() => element.classList.remove("active"), 110);
    }

    return fetch("/button/" + id + "/press", { method: "POST", cache: "no-store", signal });
  }

  function sequenceEls() {
    return {
      hud: document.getElementById("sequence-hud"),
      now: document.getElementById("sequence-now"),
      next: document.getElementById("sequence-next"),
      nextWrap: document.getElementById("sequence-next-wrap"),
      bar: document.getElementById("sequence-bar"),
      caption: document.getElementById("sequence-caption"),
      shell: document.querySelector(".remote-shell")
    };
  }

  function resetCountdownBar() {
    const { bar } = sequenceEls();
    if (!bar) {
      return;
    }
    bar.style.transition = "none";
    bar.style.transform = "scaleX(1)";
  }

  function stepName(step) {
    if (!step) {
      return "";
    }
    if (step.type === "delay") {
      return (step.ms / 1000).toFixed(1) + "s wait";
    }
    return step.label || prettyLabel(step.id);
  }

  function showSequenceHud(nowText, nextText, caption) {
    const { hud, now, next, nextWrap, caption: captionEl } = sequenceEls();
    if (!hud) {
      return;
    }
    hud.hidden = false;
    now.textContent = nowText;
    if (nextText) {
      nextWrap.hidden = false;
      next.textContent = nextText;
    } else {
      nextWrap.hidden = true;
      next.textContent = "";
    }
    captionEl.textContent = caption;
  }

  function hideSequenceHud() {
    const { hud } = sequenceEls();
    if (hud) {
      hud.hidden = true;
    }
    resetCountdownBar();
  }

  function setUiLocked(locked) {
    sequenceBusy = locked;
    const app = document.getElementById("sky-app");
    const { shell } = sequenceEls();
    if (app) {
      app.classList.toggle("is-locked", locked);
    }
    if (shell) {
      shell.classList.toggle("is-locked", locked);
      shell.setAttribute("aria-busy", locked ? "true" : "false");
    }
    setLiveTvDisabled(locked);
    if (locked && document.activeElement && document.activeElement.blur) {
      document.activeElement.blur();
    }
    if (!locked) {
      hideSequenceHud();
    }
  }

  async function countdownBar(ms, nextName) {
    const { bar, caption: captionEl } = sequenceEls();
    const started = performance.now();

    if (bar) {
      bar.style.transition = "none";
      bar.style.transform = "scaleX(1)";
    }

    while (true) {
      const elapsed = performance.now() - started;
      const remainingMs = Math.max(0, ms - elapsed);
      if (bar) {
        bar.style.transform = "scaleX(" + (ms > 0 ? remainingMs / ms : 0) + ")";
      }

      const caption = nextName
        ? "Wait " + (remainingMs / 1000).toFixed(1) + "s then " + nextName
        : "Wait " + (remainingMs / 1000).toFixed(1) + "s";
      if (captionEl) {
        captionEl.textContent = caption;
      }
      setStatus(caption, "", 0);

      if (elapsed >= ms) {
        break;
      }

      await sleep(Math.min(40, Math.max(remainingMs, 1)));
    }
  }

  async function sendAndWait(id) {
    const element = document.querySelector(`[data-command="${id}"]`);
    const controller = new AbortController();
    const timer = window.setTimeout(() => controller.abort(), 8000);
    try {
      const response = await postCommand(id, element, controller.signal);
      if (!response.ok) {
        throw new Error(prettyLabel(id) + " HTTP " + response.status);
      }
      await response.text();
    } catch (error) {
      if (error && error.name === "AbortError") {
        throw new Error(prettyLabel(id) + " timed out");
      }
      throw error;
    } finally {
      window.clearTimeout(timer);
    }
  }

  async function runSequence(steps, doneMessage) {
    if (sequenceBusy) {
      return;
    }

    setUiLocked(true);
    try {
      for (let index = 0; index < steps.length; index++) {
        const step = steps[index];
        const name = stepName(step);
        const nextName = stepName(steps[index + 1]);

        if (step.type === "delay") {
          showSequenceHud(name, nextName, "Wait");
          setStatus("Wait", "", 0);
          await countdownBar(step.ms, nextName);
          continue;
        }

        if (step.type !== "press" || !step.id) {
          throw new Error("Unknown sequence step");
        }

        showSequenceHud(name, nextName, "Sending " + name);
        setStatus("Sending " + name, "", 0);
        await sendAndWait(step.id);
      }
      setStatus(doneMessage, "ok");
    } catch (error) {
      console.error("Sequence failed:", error);
      setStatus("Failed: " + (error && error.message ? error.message : error), "error", 8000);
    } finally {
      setUiLocked(false);
    }
  }

  async function press(id, element = null) {
    if (sequenceBusy) {
      return;
    }

    sequenceBusy = true;
    try {
      const response = await postCommand(id, element);
      if (!response.ok) {
        throw new Error(prettyLabel(id) + " HTTP " + response.status);
      }
      setStatus(prettyLabel(id), "ok");
    } catch (error) {
      console.error("Sky Stream command failed:", error);
      setStatus("Failed: " + (error && error.message ? error.message : id), "error", 5000);
    } finally {
      sequenceBusy = false;
    }
  }

  function tvGuide() {
    return runSequence(guideSteps(waits), "TV Guide");
  }

  function setLiveTvDisabled(disabled) {
    const search = document.getElementById("live-tv-search");
    const select = document.getElementById("live-tv-select");
    if (search) {
      search.disabled = disabled;
    }
    if (select) {
      select.disabled = disabled;
    }
  }

  function escapeHtml(text) {
    return String(text)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  function channelOptionsHtml(filter) {
    const query = String(filter || "").trim().toLowerCase();
    const groups = [];
    const indexByCat = {};

    CHANNELS.forEach(([number, name, category]) => {
      if (query && !name.toLowerCase().includes(query) && !String(number).includes(query)) {
        return;
      }

      let groupIndex = indexByCat[category];
      if (groupIndex == null) {
        groupIndex = groups.length;
        indexByCat[category] = groupIndex;
        groups.push({ category, channels: [] });
      }

      groups[groupIndex].channels.push([number, name]);
    });

    let html = '<option value="">Live TV…</option>';
    groups.forEach(({ category, channels }) => {
      html += `<optgroup label="${escapeHtml(category)}">`;
      channels.forEach(([number, name]) => {
        html += `<option value="${number}">${number} ${escapeHtml(name)}</option>`;
      });
      html += "</optgroup>";
    });
    return html;
  }

  async function tuneChannel(number) {
    if (sequenceBusy) {
      return;
    }

    const digits = String(number);
    if (!/^[1-9]\d{0,3}$/.test(digits)) {
      return;
    }

    const steps = guideSteps(waits);
    [...digits].forEach(digit => {
      steps.push(key("sky_stream_" + digit));
      steps.push(wait(waits.betweenDigits));
    });
    steps.push(wait(waits.beforeTuneOk));
    steps.push(key("sky_stream_ok", "ok"));
    steps.push(wait(waits.betweenTuneOks));
    steps.push(key("sky_stream_ok", "ok"));

    await runSequence(steps, "Live TV " + digits);

    const select = document.getElementById("live-tv-select");
    if (select) {
      select.value = "";
    }
  }

  
  function fillSetupForm(current) {
    WAIT_FIELDS.forEach(field => {
      const input = document.getElementById("wait-" + field.key);
      if (input) {
        input.value = String(current[field.key] / 1000);
      }
    });
  }

  function readSetupForm() {
    const next = Object.assign({}, DEFAULT_WAITS);
    WAIT_FIELDS.forEach(field => {
      const input = document.getElementById("wait-" + field.key);
      if (!input) {
        return;
      }
      next[field.key] = clampWaitMs(Number(input.value) * 1000, DEFAULT_WAITS[field.key]);
    });
    return next;
  }

  function renderMacroRail() {
    const rail = document.getElementById("macro-rail");
    if (!rail) {
      return;
    }
    if (!macros.length) {
      rail.innerHTML = '<div class="macro-empty">Macros show here. Open Setup to make one.</div>';
      return;
    }
    rail.innerHTML = macros.map(item => (
      '<button type="button" class="macro-chip" data-macro-id="' +
      escapeHtml(item.id) +
      '">' +
      escapeHtml(item.name) +
      "</button>"
    )).join("");
    rail.querySelectorAll("[data-macro-id]").forEach(button => {
      button.addEventListener("click", () => {
        if (sequenceBusy) {
          return;
        }
        runMacro(button.getAttribute("data-macro-id"));
      });
    });
  }

  function renderSavedMacros() {
    const list = document.getElementById("macro-saved");
    if (!list) {
      return;
    }
    if (!macros.length) {
      list.innerHTML = '<div class="macro-empty">None yet</div>';
      return;
    }
    list.innerHTML = macros.map(item => (
      '<div class="macro-saved-row">' +
      "<span>" + escapeHtml(item.name) + "</span>" +
      '<button type="button" class="macro-mini" data-edit-macro="' + escapeHtml(item.id) + '">Edit</button>' +
      '<button type="button" class="macro-mini" data-delete-macro="' + escapeHtml(item.id) + '">Del</button>' +
      "</div>"
    )).join("");
  }

  function renderMacroSelect() {
    const select = document.getElementById("macro-add-macro");
    if (!select) {
      return;
    }
    const others = macros.filter(item => item.id !== editor.id);
    select.innerHTML = others.length
      ? others.map(item => '<option value="' + escapeHtml(item.id) + '">' + escapeHtml(item.name) + "</option>").join("")
      : '<option value="">No other macros</option>';
    select.disabled = !others.length;
  }

  function renderEditorSteps() {
    const list = document.getElementById("macro-steps");
    const nameInput = document.getElementById("macro-name");
    if (nameInput && document.activeElement !== nameInput) {
      nameInput.value = editor.name;
    }
    if (!list) {
      return;
    }
    if (!editor.steps.length) {
      list.innerHTML = '<div class="macro-empty">Add a key, wait, TV Guide, or another macro</div>';
      return;
    }
    list.innerHTML = editor.steps.map((step, index) => (
      '<div class="macro-step">' +
      "<span>" + escapeHtml(draftStepLabel(step)) + "</span>" +
      '<button type="button" class="macro-mini" data-step-move="' + index + '" data-dir="-1">↑</button>' +
      '<button type="button" class="macro-mini" data-step-move="' + index + '" data-dir="1">↓</button>' +
      '<button type="button" class="macro-mini" data-step-remove="' + index + '">×</button>' +
      "</div>"
    )).join("");
  }

  function refreshMacroUi() {
    renderMacroRail();
    renderSavedMacros();
    renderMacroSelect();
    renderEditorSteps();
  }

  function addEditorStep(step) {
    if (editor.steps.length >= MAX_MACRO_STEPS) {
      setStatus("Macro is full", "error", 4000);
      return;
    }
    const normalized = normalizeStep(step);
    if (!normalized) {
      return;
    }
    editor.steps.push(normalized);
    renderEditorSteps();
  }

  function bindSetup() {
    const toggle = document.getElementById("setup-toggle");
    const panel = document.getElementById("setup-panel");
    fillSetupForm(waits);
    refreshMacroUi();

    toggle.addEventListener("click", () => {
      if (sequenceBusy) {
        return;
      }
      const opening = panel.hidden;
      panel.hidden = !opening;
      toggle.setAttribute("aria-expanded", opening ? "true" : "false");
      if (opening) {
        fillSetupForm(waits);
        refreshMacroUi();
      }
    });

    document.getElementById("setup-save").addEventListener("click", () => {
      waits = readSetupForm();
      persistConfig();
      fillSetupForm(waits);
      setStatus("Delays saved", "ok");
    });

    document.getElementById("setup-defaults").addEventListener("click", () => {
      waits = Object.assign({}, DEFAULT_WAITS);
      persistConfig();
      fillSetupForm(waits);
      setStatus("Delay defaults restored", "ok");
    });

    document.getElementById("macro-name").addEventListener("input", event => {
      editor.name = event.target.value.slice(0, 24);
    });

    document.getElementById("macro-add-key-btn").addEventListener("click", () => {
      addEditorStep({ type: "press", id: document.getElementById("macro-add-key").value });
    });

    document.getElementById("macro-add-wait-btn").addEventListener("click", () => {
      const seconds = Number(document.getElementById("macro-add-wait").value);
      addEditorStep({ type: "delay", ms: clampWaitMs(seconds * 1000, 1000) });
    });

    document.getElementById("macro-add-guide-btn").addEventListener("click", () => {
      addEditorStep({ type: "guide" });
    });

    document.getElementById("macro-add-macro-btn").addEventListener("click", () => {
      const macroId = document.getElementById("macro-add-macro").value;
      if (!macroId) {
        return;
      }
      addEditorStep({ type: "macro", macroId: macroId });
    });

    document.getElementById("macro-steps").addEventListener("click", event => {
      const remove = event.target.closest("[data-step-remove]");
      if (remove) {
        editor.steps.splice(Number(remove.getAttribute("data-step-remove")), 1);
        renderEditorSteps();
        return;
      }
      const move = event.target.closest("[data-step-move]");
      if (!move) {
        return;
      }
      const index = Number(move.getAttribute("data-step-move"));
      const dir = Number(move.getAttribute("data-dir"));
      const next = index + dir;
      if (next < 0 || next >= editor.steps.length) {
        return;
      }
      const swap = editor.steps[index];
      editor.steps[index] = editor.steps[next];
      editor.steps[next] = swap;
      renderEditorSteps();
    });

    document.getElementById("macro-saved").addEventListener("click", event => {
      const edit = event.target.closest("[data-edit-macro]");
      if (edit) {
        const macro = macros.find(item => item.id === edit.getAttribute("data-edit-macro"));
        if (!macro) {
          return;
        }
        editor = {
          id: macro.id,
          name: macro.name,
          steps: macro.steps.map(step => Object.assign({}, step))
        };
        document.getElementById("macro-name").value = editor.name;
        refreshMacroUi();
        return;
      }
      const del = event.target.closest("[data-delete-macro]");
      if (!del) {
        return;
      }
      const id = del.getAttribute("data-delete-macro");
      macros = macros.filter(item => item.id !== id);
      macros.forEach(item => {
        item.steps = item.steps.filter(step => !(step.type === "macro" && step.macroId === id));
      });
      if (editor.id === id) {
        editor = blankEditor();
        document.getElementById("macro-name").value = "";
      }
      persistConfig();
      refreshMacroUi();
      setStatus("Macro deleted", "ok");
    });

    document.getElementById("macro-new").addEventListener("click", () => {
      editor = blankEditor();
      document.getElementById("macro-name").value = "";
      refreshMacroUi();
    });

    document.getElementById("macro-save-btn").addEventListener("click", () => {
      const name = String(document.getElementById("macro-name").value || "").trim().slice(0, 24);
      if (!name) {
        setStatus("Name the macro", "error", 4000);
        return;
      }
      if (!editor.steps.length) {
        setStatus("Add at least one step", "error", 4000);
        return;
      }
      const record = normalizeMacro({
        id: editor.id || newMacroId(),
        name: name,
        steps: editor.steps
      });
      const index = macros.findIndex(item => item.id === record.id);
      if (index >= 0) {
        macros[index] = record;
      } else {
        if (macros.length >= MAX_MACROS) {
          setStatus("Maximum " + MAX_MACROS + " macros", "error", 4000);
          return;
        }
        macros.push(record);
      }
      editor = {
        id: record.id,
        name: record.name,
        steps: record.steps.map(step => Object.assign({}, step))
      };
      persistConfig();
      refreshMacroUi();
      setStatus("Macro saved", "ok");
    });
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
    injectStyles();
    injectLiveTvStyles();
    document.title = "Sky Stream IR Remote";

    document.body.innerHTML = `
      <main id="sky-app">
        <aside id="macro-rail" class="macro-rail" aria-label="Macros"></aside>
        <section class="remote-shell" aria-label="Sky Stream remote control">

          <div class="device-head">
            <div class="device-title">
              <i class="device-led"></i>
              <span class="device-host">${location.hostname}</span>
            </div>
            <button type="button" id="setup-toggle" class="setup-toggle" aria-expanded="false" aria-controls="setup-panel">Setup</button>
          </div>

          <div id="sequence-hud" class="sequence-hud" hidden>
            <div class="sequence-labels">
              <div>
                <span class="sequence-kicker">Now</span>
                <strong id="sequence-now"></strong>
              </div>
              <div id="sequence-next-wrap">
                <span class="sequence-kicker">Next</span>
                <strong id="sequence-next"></strong>
              </div>
            </div>
            <div class="sequence-track" aria-hidden="true">
              <i id="sequence-bar" class="sequence-bar"></i>
            </div>
            <div id="sequence-caption" class="sequence-caption"></div>
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

          <div class="live-tv">
            <div class="live-tv-label">Live TV</div>
            <input
              type="search"
              id="live-tv-search"
              class="live-tv-search"
              placeholder="Find channel"
              autocomplete="off"
              spellcheck="false"
              aria-label="Search channels">
            <select id="live-tv-select" class="live-tv-select" aria-label="Live TV">
              ${channelOptionsHtml("")}
            </select>
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
          <div class="footer">${TV_GUIDE_FOOTER}</div>

          <div id="setup-panel" class="setup-panel" hidden>
            <p class="setup-hint">Seconds for this browser only. The ESP32 only sends IR.</p>
            ${WAIT_FIELDS.map(field => `
            <div class="setup-row">
              <label for="wait-${field.key}">${field.label}</label>
              <input id="wait-${field.key}" type="number" min="0" max="30" step="0.1" inputmode="decimal" data-wait="${field.key}" aria-label="${field.label} in seconds">
            </div>`).join("")}
            <div class="setup-actions">
              <button type="button" id="setup-save">Save</button>
              <button type="button" id="setup-defaults">Defaults</button>
            </div>

            <h3 class="setup-heading">Macros</h3>
            <p class="setup-hint">Quick buttons sit beside the remote. Chain a key, a wait, TV Guide, or another macro.</p>
            <div id="macro-saved" class="macro-saved"></div>
            <input id="macro-name" type="text" maxlength="24" placeholder="Macro name" autocomplete="off" spellcheck="false">
            <div id="macro-steps"></div>
            <div class="macro-add-row">
              <select id="macro-add-key">${KEY_OPTIONS.map(option =>
                '<option value="' + option[0] + '">' + option[1] + "</option>"
              ).join("")}</select>
              <button type="button" class="macro-mini" id="macro-add-key-btn">+ Key</button>
            </div>
            <div class="macro-add-row">
              <input id="macro-add-wait" type="number" min="0" max="30" step="0.1" value="1" inputmode="decimal" aria-label="Wait seconds">
              <button type="button" class="macro-mini" id="macro-add-wait-btn">+ Wait</button>
            </div>
            <div class="macro-add-row">
              <select id="macro-add-macro"></select>
              <button type="button" class="macro-mini" id="macro-add-macro-btn">+ Macro</button>
            </div>
            <div class="setup-actions">
              <button type="button" id="macro-add-guide-btn">+ TV Guide</button>
              <button type="button" id="macro-new">New</button>
            </div>
            <div class="setup-actions">
              <button type="button" id="macro-save-btn">Save macro</button>
            </div>
          </div>
        </section>
      </main>
    `;

    document.querySelectorAll("[data-command]").forEach(element => {
      element.addEventListener("click", event => {
        event.preventDefault();
        press(element.dataset.command, element);
      });
    });

    document.getElementById("sky-app").addEventListener("click", event => {
      if (!sequenceBusy) {
        return;
      }
      event.preventDefault();
      event.stopPropagation();
    }, true);

    document.getElementById("tv-guide").addEventListener("click", event => {
      event.preventDefault();
      tvGuide();
    });

    const liveTvSearch = document.getElementById("live-tv-search");
    const liveTvSelect = document.getElementById("live-tv-select");

    liveTvSearch.addEventListener("input", () => {
      const current = liveTvSelect.value;
      liveTvSelect.innerHTML = channelOptionsHtml(liveTvSearch.value);
      if ([...liveTvSelect.options].some(option => option.value === current)) {
        liveTvSelect.value = current;
      }
    });

    liveTvSelect.addEventListener("change", () => {
      if (!liveTvSelect.value) {
        return;
      }
      tuneChannel(liveTvSelect.value);
    });

    bindSetup();

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
      if (event.metaKey || event.ctrlKey || event.altKey) {
        return;
      }

      if (sequenceBusy) {
        event.preventDefault();
        return;
      }

      if (event.repeat) {
        return;
      }

      if (event.target && /^(INPUT|SELECT|TEXTAREA)$/.test(event.target.tagName)) {
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
