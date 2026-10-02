(() => {
  'use strict';
  const $ = id => document.getElementById(id), audio = $('audio'), wave = $('wave');
  const handles = [$('trimStartHandle'), $('trimEndHandle')];
  let start = 0, end = 0, drag = null;
  const state = () => window.msmEditor.getPreviewState();
  const ready = () => !!state().projectKey && !state().busy && audio.readyState >= 1 && state().duration > 0;
  const post = message => window.chrome?.webview?.postMessage({...message, projectKey: state().projectKey});
  const clamp = (n, lo, hi) => Math.max(lo, Math.min(hi, n));
  const gap = () => Math.min(.03, end - start || .03, state().duration);
  function render() {
    const duration = state().duration, loaded = duration > 0 && audio.readyState >= 1;
    const left = duration ? start / duration * 100 : 0, right = duration ? end / duration * 100 : 100;
    $('trimShadeStart').style.width = left + '%';
    $('trimShadeEnd').style.width = (100 - right) + '%';
    handles.forEach((handle, i) => {
      const value = i ? end : start;
      handle.hidden = !loaded; handle.disabled = !ready();
      handle.style.left = `clamp(0px, calc(${i ? right : left}% - 9px), calc(100% - 18px))`;
      handle.setAttribute('aria-valuenow', value.toFixed(3));
      handle.setAttribute('aria-valuemin', (i ? start + gap() : 0).toFixed(3));
      handle.setAttribute('aria-valuemax', (i ? duration : end - gap()).toFixed(3));
      handle.setAttribute('aria-valuetext', value.toFixed(3) + ' seconds');
      handle.title = `${i ? 'End' : 'Start'}: ${value.toFixed(3)} s. Drag to trim; arrow keys adjust, Shift = 1 second.`;
    });
    $('clipSelection').hidden = !loaded || (start < .001 && end >= duration - .001);
    $('clipSelection').disabled = !ready();
    $('clipSelection').textContent = `Clip selection · ${(end - start).toFixed(3)} s`;
  }
  function setRange(from, to, notify = false) {
    const duration = state().duration;
    if (!Number.isFinite(from) || !Number.isFinite(to) || !duration || from < 0 || from >= duration || to > duration + .001 || to <= from) return;
    start = from;
    end = Math.min(to, duration);
    render();
    if (notify) post({type: 'trimRange', start, end});
  }
  function move(index, value) {
    if (index) setRange(start, clamp(value, start + gap(), state().duration), true);
    else setRange(clamp(value, 0, end - gap()), end, true);
  }
  function finish(event) {
    if (!drag || (event && event.pointerId !== drag.pointerId)) return;
    const handle = handles[drag.index];
    if (handle.hasPointerCapture(drag.pointerId)) handle.releasePointerCapture(drag.pointerId);
    drag = null;
  }
  handles.forEach((handle, index) => {
    handle.addEventListener('pointerdown', event => {
      if (!ready() || event.button !== 0) return;
      event.preventDefault(); event.stopPropagation();
      handle.focus();
      drag = {index, pointerId: event.pointerId, x: event.clientX, value: index ? end : start};
      try { handle.setPointerCapture(event.pointerId); } catch { /* Synthetic integration-test pointers have no capture. */ }
    });
    handle.addEventListener('pointermove', event => {
      if (!drag || drag.pointerId !== event.pointerId) return;
      if (!ready()) { finish(); return; }
      const width = wave.getBoundingClientRect().width;
      if (width) move(index, drag.value + (event.clientX - drag.x) / width * state().duration);
    });
    for (const name of ['pointerup', 'pointercancel', 'lostpointercapture']) handle.addEventListener(name, finish);
    handle.addEventListener('keydown', event => {
      if (!ready()) return;
      let value = index ? end : start;
      if (event.key === 'ArrowLeft' || event.key === 'ArrowDown') value -= event.shiftKey ? 1 : .01;
      else if (event.key === 'ArrowRight' || event.key === 'ArrowUp') value += event.shiftKey ? 1 : .01;
      else if (event.key === 'Home') value = index ? start + gap() : 0;
      else if (event.key === 'End') value = index ? state().duration : end - gap();
      else return;
      event.preventDefault(); event.stopPropagation(); move(index, value);
    });
  });
  audio.addEventListener('loadedmetadata', () => { finish(); start = 0; end = state().duration; render(); });
  audio.addEventListener('emptied', () => { finish(); start = end = 0; render(); });
  window.addEventListener('blur', () => finish());
  $('clipSelection').onclick = () => { if (ready()) post({type: 'studioCommand', command: 'Trim'}); };
  window.chrome?.webview?.addEventListener('message', event => {
    const message = event.data;
    if (message.type === 'trimRange' && message.projectKey === state().projectKey) setRange(message.start, message.end);
    if (message.type === 'busy') { if (!ready()) finish(); render(); }
  });
  window.msmTrim = {getRange: () => ({start, end}), refreshControls: render};
  render();
})();
