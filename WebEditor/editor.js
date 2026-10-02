(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const audio = $('audio'), canvas = $('chart'), ctx = canvas.getContext('2d');
  const wave = $('wave'), wctx = wave.getContext('2d'), viewport = $('chartViewport');
  const space = $('chartSpace'), playBtn = $('play'), bpmInput = $('bpm'), offsetInput = $('offset');
  let notes = [], selected = new Set(), dragging = null, projectKey = '', duration = 0, busy = false;
  let pixelsPerSecond = 90, peaks = null, loadVersion = 0, loadingController = null, dirty = false, audioOnsets = [];
  let undoStack = [], redoStack = [], previousSettings = '', waveMessage = 'Import audio to see its waveform';
  let lastFrame = 0, needsDraw = true, revision = 0, rightClickHandled = false;
  const rowHeight = 22, minPitch = 47, maxPitch = 73, rows = maxPitch - minPitch + 1;
  const clamp = (v, a, b) => Math.max(a, Math.min(b, v));
  const clone = value => JSON.parse(JSON.stringify(value));
  const post = message => window.chrome?.webview?.postMessage({...message, projectKey});
  const bpm = () => clamp(Number(bpmInput.value) || 120, 20, 400);
  const offset = () => Number(offsetInput.value) || 0;
  const snapSeconds = () => $('grid').checked && Number($('snap').value) ? 60 / bpm() * 4 / Number($('snap').value) : 0;
  const snapTime = t => snapSeconds() ? Math.round(t / snapSeconds()) * snapSeconds() : t;
  const freePitch = y => clamp(maxPitch + .5 - y / rowHeight, minPitch, maxPitch);
  const placedPitch = y => $('grid').checked ? clamp(maxPitch - Math.floor(y / rowHeight), minPitch, maxPitch) : freePitch(y);
  const pathPoints = n => n.curvePoints?.length >= 2 ? n.curvePoints : [{position: 0, pitch: n.pitch}, {position: 1, pitch: n.endPitch}];
  const hasPath = n => !!n.curvePoints?.length || n.endPitch !== n.pitch;
  const pitchY = pitch => (maxPitch - pitch) * rowHeight + rowHeight / 2;
  const polyline = n => pathPoints(n).map(p => ({x: (n.time + offset() + p.position * n.length) * pixelsPerSecond, y: pitchY(p.pitch)}));
  function overlapCount() {
    let end = -1, count = 0;
    for (const n of [...notes].sort((a, b) => a.time - b.time)) {
      if (n.time < end - 1e-8) count++;
      end = Math.max(end, n.time + n.length);
    }
    return count;
  }
  const noteLength = () => {
    const value = Number($('noteLength').value);
    return Number.isFinite(value) && value > 0 ? clamp(value, .03, 3600) : .25;
  };
  const format = seconds => {
    const ms = Math.round(Math.max(0, seconds || 0) * 1000);
    return String(Math.floor(ms / 60000)).padStart(2, '0') + ':' +
      String(Math.floor(ms / 1000) % 60).padStart(2, '0') + '.' + String(ms % 1000).padStart(3, '0');
  };
  function status(message, error = false, passive = false) {
    $('audioStatus').textContent = message;
    $('audioStatus').classList.toggle('error', error);
    post({type: 'audioStatus', message, error, passive});
  }
  function updateControls() {
    const ready = !!projectKey && !busy;
    const editing = ready && (!window.msmPreview || window.msmPreview.getState().mode === 'editor');
    window.msmTrim?.refreshControls();
    $('viewMode').disabled = !ready;
    playBtn.disabled = !ready || !audio.getAttribute('src') || audio.readyState < 1;
    $('stop').disabled = !ready || !audio.getAttribute('src');
    $('save').disabled = !ready;
    for (const id of ['bpm', 'offset', 'zoom', 'tool', 'noteLength', 'grid']) $(id).disabled = !editing;
    $('snap').disabled = !editing || !$('grid').checked;
    $('applyLength').disabled = !editing || !selected.size;
    $('undo').disabled = !editing || !undoStack.length;
    $('redo').disabled = !editing || !redoStack.length;
    $('undo').title = 'Undo ' + (undoStack.at(-1)?.label || 'note edit') + ' (Ctrl+Z)';
    $('redo').title = 'Redo ' + (redoStack.at(-1)?.label || 'note edit') + ' (Ctrl+Y)';
    $('delete').disabled = !editing || !selected.size;
    const overlaps = overlapCount();
    $('singleLine').disabled = !editing || !overlaps;
    $('selectionStatus').textContent = (selected.size ? selected.size + (selected.size === 1 ? ' note selected' : ' notes selected') : 'No notes selected') +
      (overlaps ? ' · ' + overlaps + ' overlapping' : '');
    $('selectionStatus').title = $('selectionStatus').textContent;
    post({type: 'editorState', noteCount: notes.length, selectedCount: editing ? selected.size : 0,
      canUndo: editing && !!undoStack.length, canRedo: editing && !!redoStack.length,
      audioReady: !!audio.getAttribute('src') && audio.readyState >= 1, duration, revision});
  }
  function markDirty() {
    if (undoStack.length > 100) undoStack = undoStack.slice(-100);
    dirty = true;
    revision++;
    $('save').textContent = 'Save chart… *';
    post({type: 'dirty', revision});
    updateControls();
    needsDraw = true;
  }
  function state() { return {notes: clone(notes), bpm: bpm(), offset: offset(), showGrid: $('grid').checked, selected: [...selected]}; }
  function remember() {
    undoStack.push(state());
    if (undoStack.length > 100) undoStack.shift();
    redoStack = [];
  }
  function restore(value) {
    notes = clone(value.notes);
    bpmInput.value = value.bpm;
    offsetInput.value = value.offset;
    $('grid').checked = value.showGrid ?? true;
    previousSettings = JSON.stringify({bpm: bpm(), offset: offset()});
    selected = new Set((value.selected || []).filter(id => notes.some(n => n.id === id)));
    resize();
    markDirty();
  }
  function undo() {
    if (busy || !undoStack.length) return;
    endDrag();
    const entry = undoStack.pop();
    if (entry.projectEdit) {
      redoStack.push(entry); busy = true; updateControls();
      post({type: 'projectHistory', id: entry.projectEdit, direction: 'undo'});
    } else { redoStack.push(state()); restore(entry); }
  }
  function redo() {
    if (busy || !redoStack.length) return;
    endDrag();
    const entry = redoStack.pop();
    if (entry.projectEdit) {
      undoStack.push(entry); busy = true; updateControls();
      post({type: 'projectHistory', id: entry.projectEdit, direction: 'redo'});
    } else { undoStack.push(state()); restore(entry); }
  }
  function deleteSelected() {
    if (!selected.size || busy) return;
    endDrag();
    remember();
    notes = notes.filter(n => !selected.has(n.id));
    selected.clear();
    markDirty();
    resize();
  }
  function selectAll() {
    if (busy || !projectKey) return;
    endDrag(); selected = new Set(notes.map(n => n.id)); updateControls(); needsDraw = true;
  }
  function clearSelection() { cancelDrag(); selected.clear(); updateControls(); needsDraw = true; }
  function timelineLength() {
    return Math.max(10, duration, ...notes.map(n => n.time + n.length + offset() + 2));
  }
  function resize() {
    const ratio = window.devicePixelRatio || 1;
    space.style.width = Math.max(viewport.clientWidth, timelineLength() * pixelsPerSecond) + 'px';
    space.style.height = Math.max(viewport.clientHeight, rows * rowHeight) + 'px';
    canvas.style.width = viewport.clientWidth + 'px';
    canvas.style.height = viewport.clientHeight + 'px';
    canvas.width = Math.max(1, Math.round(viewport.clientWidth * ratio));
    canvas.height = Math.max(1, Math.round(viewport.clientHeight * ratio));
    wave.width = Math.max(1, Math.round(wave.clientWidth * ratio));
    wave.height = Math.max(1, Math.round(wave.clientHeight * ratio));
    needsDraw = true;
    drawWave();
  }
  function draw() {
    const ratio = window.devicePixelRatio || 1;
    ctx.setTransform(ratio, 0, 0, ratio, 0, 0);
    const w = canvas.width / ratio, h = canvas.height / ratio, sx = viewport.scrollLeft, sy = viewport.scrollTop;
    ctx.clearRect(0, 0, w, h);
    ctx.fillStyle = '#101218'; ctx.fillRect(0, 0, w, h);
    ctx.font = '11px Segoe UI';
    if ($('grid').checked) for (let p = maxPitch; p >= minPitch; p--) {
      const y = (maxPitch - p) * rowHeight - sy;
      if (y + rowHeight < 0 || y > h) continue;
      ctx.fillStyle = p % 12 === 0 ? '#1b2230' : p % 2 === 0 ? '#141a24' : '#10151d';
      ctx.fillRect(0, y, w, rowHeight);
      ctx.strokeStyle = '#252c39'; ctx.lineWidth = 1;
      ctx.beginPath(); ctx.moveTo(0, y + rowHeight - .5); ctx.lineTo(w, y + rowHeight - .5); ctx.stroke();
    }
    const beat = 60 / bpm(), startBeat = Math.floor((sx / pixelsPerSecond - offset()) / beat);
    if ($('grid').checked) for (let n = startBeat; n * beat + offset() <= (sx + w) / pixelsPerSecond; n++) {
      const x = (n * beat + offset()) * pixelsPerSecond - sx;
      ctx.strokeStyle = n % 4 === 0 ? '#46536c' : '#283243'; ctx.lineWidth = 1;
      ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x, h); ctx.stroke();
    }
    for (const n of notes) {
      const x = (n.time + offset()) * pixelsPerSecond - sx, y = (maxPitch - n.pitch) * rowHeight + 3 - sy;
      const nw = Math.max(9, n.length * pixelsPerSecond), nh = rowHeight - 6;
      if (x + nw < 0 || x > w) continue;
      if (hasPath(n)) {
        const line = polyline(n);
        ctx.strokeStyle = selected.has(n.id) ? '#a3b7ff' : '#708fff';
        ctx.lineWidth = 12; ctx.lineJoin = 'round'; ctx.lineCap = 'round'; ctx.beginPath();
        line.forEach((p, i) => i ? ctx.lineTo(p.x - sx, p.y - sy) : ctx.moveTo(p.x - sx, p.y - sy)); ctx.stroke();
        ctx.strokeStyle = '#dce6ff'; ctx.lineWidth = 2; ctx.stroke(); ctx.lineCap = 'butt';
        const last = line[line.length - 1]; ctx.fillStyle = '#e1e9ff';
        ctx.fillRect(last.x - sx - 3, last.y - sy - 8, 6, 16);
        ctx.beginPath(); ctx.arc(line[0].x - sx, line[0].y - sy, 4, 0, Math.PI * 2); ctx.fill();
        continue;
      }
      ctx.fillStyle = selected.has(n.id) ? '#a3b7ff' : '#708fff';
      ctx.fillRect(x, y, nw, nh);
      ctx.fillStyle = '#e1e9ff'; ctx.fillRect(x + nw - 4, y, 4, nh);
      if (n.endPitch !== n.pitch) {
        ctx.strokeStyle = '#c5d2ff'; ctx.lineWidth = 2; ctx.beginPath();
        ctx.moveTo(x, y + nh / 2); ctx.lineTo(x + nw, (maxPitch - n.endPitch) * rowHeight + 3 + nh / 2 - sy); ctx.stroke();
      }
    }
    const x = audio.currentTime * pixelsPerSecond - sx;
    ctx.strokeStyle = '#ffdb89'; ctx.lineWidth = 2;
    ctx.beginPath(); ctx.moveTo(x, 0); ctx.lineTo(x, h); ctx.stroke();
    if ($('grid').checked) {
      ctx.fillStyle = '#111722'; ctx.fillRect(0, 0, 34, h);
      ctx.fillStyle = '#8895ab';
      for (let p = maxPitch; p >= minPitch; p--) ctx.fillText(String(p), 6, (maxPitch - p) * rowHeight + 15 - sy);
    }
    if (dragging?.mode === 'marquee') {
      const left = Math.min(dragging.startX, dragging.x) - sx, top = Math.min(dragging.startY, dragging.y) - sy;
      const width = Math.abs(dragging.x - dragging.startX), height = Math.abs(dragging.y - dragging.startY);
      ctx.fillStyle = 'rgba(112,143,255,.16)'; ctx.fillRect(left, top, width, height);
      ctx.strokeStyle = '#b5c7ff'; ctx.lineWidth = 1; ctx.setLineDash([5, 3]);
      ctx.strokeRect(left + .5, top + .5, width, height); ctx.setLineDash([]);
    }
  }
  function drawWave() {
    const w = wave.width, h = wave.height;
    wctx.clearRect(0, 0, w, h);
    wctx.fillStyle = '#111722'; wctx.fillRect(0, 0, w, h);
    if (!peaks) {
      wctx.fillStyle = '#98a8c2'; wctx.font = ((window.devicePixelRatio || 1) * 13) + 'px Segoe UI';
      wctx.textAlign = 'center'; wctx.fillText(waveMessage, w / 2, h / 2); return;
    }
    wctx.strokeStyle = '#7597de'; wctx.lineWidth = 1;
    wctx.beginPath();
    for (let x = 0; x < w; x += 2) {
      const pair = peaks[Math.min(peaks.length - 1, Math.floor(x / w * peaks.length))];
      wctx.moveTo(x, h / 2 - pair[1] * h * .43); wctx.lineTo(x, h / 2 - pair[0] * h * .43);
    }
    wctx.stroke();
    if (duration) {
      wctx.strokeStyle = '#ffdb89'; wctx.lineWidth = Math.max(1, window.devicePixelRatio || 1);
      wctx.beginPath();
      for (const time of audioOnsets) {
        const x = time / duration * w;
        wctx.moveTo(x, h); wctx.lineTo(x, h - Math.max(8, h * .14));
      }
      wctx.stroke();
    }
  }
  async function loadWave(url, version, signal) {
    try {
      const response = await fetch(url, {signal});
      if (!response.ok) throw new Error('Cannot read waveform');
      if (Number(response.headers.get('Content-Length')) > 150 * 1024 * 1024) throw new Error('Audio is too large for waveform decoding');
      const bytes = await response.arrayBuffer();
      if (version !== loadVersion) return;
      const ac = new AudioContext();
      let decoded;
      try { decoded = await ac.decodeAudioData(bytes); } finally { await ac.close(); }
      if (version !== loadVersion) return;
      const channels = Array.from({length: Math.min(2, decoded.numberOfChannels)}, (_, i) => decoded.getChannelData(i));
      const count = 1800, step = Math.max(1, Math.ceil(decoded.length / count));
      peaks = Array.from({length: count}, (_, i) => {
        let low = 0, high = 0;
        for (const channel of channels) {
          for (let j = i * step; j < Math.min(channel.length, (i + 1) * step); j++) {
            low = Math.min(low, channel[j]); high = Math.max(high, channel[j]);
          }
        }
        return [low, high];
      });
      drawWave();
    } catch (error) {
      if (signal.aborted || version !== loadVersion) return;
      waveMessage = 'Waveform unavailable — playback and editing still work';
      drawWave();
    }
  }
  function loadAudio(url) {
    loadingController?.abort();
    loadingController = new AbortController();
    const version = ++loadVersion;
    audio.pause(); duration = 0; peaks = null;
    if (url) {
      waveMessage = 'Reading audio waveform…';
      status('Loading audio…');
      audio.src = url; audio.load();
      loadWave(url, version, loadingController.signal);
    } else {
      waveMessage = 'Import audio to see its waveform';
      audio.removeAttribute('src'); audio.load();
      status('No audio loaded. Import an audio file to play.');
    }
    viewport.scrollLeft = 0;
    resize(); updateControls();
  }
  function hitTest(x, y) {
    for (let i = notes.length - 1; i >= 0; i--) {
      const n = notes[i], nx = (n.time + offset()) * pixelsPerSecond, ny = (maxPitch - n.pitch) * rowHeight + 3;
      const nw = Math.max(9, n.length * pixelsPerSecond);
      if (hasPath(n)) {
        const line = polyline(n), last = line[line.length - 1];
        if (Math.abs(x - last.x) <= 6 && Math.abs(y - last.y) <= 9) return {note: n, resize: true};
        if (line.slice(1).some((p, j) => pointDistance({x, y}, line[j], p) <= 8)) return {note: n, resize: false};
        continue;
      }
      if (x >= nx && x <= nx + nw && y >= ny && y <= ny + rowHeight - 6)
        return {note: n, resize: x >= nx + nw - 6};
    }
    return null;
  }
  function point(event) {
    const rect = canvas.getBoundingClientRect();
    return {x: event.clientX - rect.left + viewport.scrollLeft, y: event.clientY - rect.top + viewport.scrollTop};
  }
  function pointDistance(p, a, b) {
    const dx = b.x - a.x, dy = b.y - a.y, squared = dx * dx + dy * dy;
    const t = squared ? clamp(((p.x - a.x) * dx + (p.y - a.y) * dy) / squared, 0, 1) : 0;
    return Math.hypot(p.x - a.x - t * dx, p.y - a.y - t * dy);
  }
  function segmentsNear(a, b, c, d, distance) {
    const cross = (x, y) => x.x * y.y - x.y * y.x;
    const ab = {x: b.x - a.x, y: b.y - a.y}, cd = {x: d.x - c.x, y: d.y - c.y};
    const ac = {x: c.x - a.x, y: c.y - a.y}, denominator = cross(ab, cd);
    if (Math.abs(denominator) > 1e-10) {
      const t = cross(ac, cd) / denominator, u = cross(ac, ab) / denominator;
      if (t >= 0 && t <= 1 && u >= 0 && u <= 1) return true;
    }
    return Math.min(pointDistance(a, c, d), pointDistance(b, c, d), pointDistance(c, a, b), pointDistance(d, a, b)) <= distance;
  }
  function segmentRect(a, b, left, top, right, bottom) {
    let enter = 0, leave = 1;
    for (const [start, delta, low, high] of [[a.x, b.x - a.x, left, right], [a.y, b.y - a.y, top, bottom]]) {
      if (delta === 0) { if (start < low || start > high) return false; }
      else {
        const near = (low - start) / delta, far = (high - start) / delta;
        enter = Math.max(enter, Math.min(near, far)); leave = Math.min(leave, Math.max(near, far));
        if (enter > leave) return false;
      }
    }
    return true;
  }
  function smoothCurve(note) {
    const source = note.curvePoints, slopes = source.slice(1).map((p, i) => (p.pitch - source[i].pitch) / (p.position - source[i].position));
    const tangents = source.map((p, i) => {
      if (!i) return slopes[0];
      if (i === source.length - 1) return slopes[slopes.length - 1];
      if (slopes[i - 1] * slopes[i] <= 0) return 0;
      const h0 = p.position - source[i - 1].position, h1 = source[i + 1].position - p.position;
      const w0 = 2 * h1 + h0, w1 = h1 + 2 * h0;
      return (w0 + w1) / (w0 / slopes[i - 1] + w1 / slopes[i]);
    });
    const sampled = [source[0]];
    for (let i = 1; i < source.length; i++) {
      const a = source[i - 1], b = source[i], span = b.position - a.position;
      const steps = Math.min(32, Math.max(2, Math.ceil(span * note.length * pixelsPerSecond / 4), Math.ceil(Math.abs(b.pitch - a.pitch) * rowHeight / 4)));
      for (let j = 1; j < steps; j++) {
        const t = j / steps, t2 = t * t, t3 = t2 * t;
        const pitch = (2 * t3 - 3 * t2 + 1) * a.pitch + (t3 - 2 * t2 + t) * span * tangents[i - 1] +
          (-2 * t3 + 3 * t2) * b.pitch + (t3 - t2) * span * tangents[i];
        sampled.push({position: a.position + span * t, pitch: clamp(pitch, Math.min(a.pitch, b.pitch), Math.max(a.pitch, b.pitch))});
      }
      sampled.push(b);
    }
    // Keep the smooth shape to within .03 semitone without thousands of redundant export segments.
    const keep = new Set([0, sampled.length - 1]), pending = [[0, sampled.length - 1]];
    while (pending.length) {
      const [first, last] = pending.pop(), a = sampled[first], b = sampled[last];
      let error = .03, index = -1;
      for (let i = first + 1; i < last; i++) {
        const p = sampled[i], expected = a.pitch + (b.pitch - a.pitch) * (p.position - a.position) / (b.position - a.position);
        if (Math.abs(p.pitch - expected) > error) { error = Math.abs(p.pitch - expected); index = i; }
      }
      if (index >= 0) { keep.add(index); pending.push([first, index], [index, last]); }
    }
    note.curvePoints = sampled.filter((_, i) => keep.has(i));
  }
  function pitchAt(note, position) {
    const points = pathPoints(note);
    for (let i = 1; i < points.length; i++) {
      if (position > points[i].position) continue;
      const a = points[i - 1], b = points[i];
      return a.pitch + (b.pitch - a.pitch) * (position - a.position) / (b.position - a.position);
    }
    return points[points.length - 1].pitch;
  }
  function shortenNote(note, length) {
    const finish = length / note.length, endPitch = pitchAt(note, finish);
    if (note.curvePoints?.length) {
      note.curvePoints = note.curvePoints.filter(p => p.position < finish).map(p => ({position: p.position / finish, pitch: p.pitch}));
      note.curvePoints.push({position: 1, pitch: endPitch});
    }
    note.endPitch = endPitch; note.length = length;
  }
  canvas.addEventListener('pointerdown', event => {
    if (!projectKey || busy) return;
    if (event.button === 2) {
      event.preventDefault();
      canvas.focus({preventScroll: true});
      endDrag();
      const {x, y} = point(event), hit = hitTest(x, y);
      dragging = {mode: 'erase', lastX: x, lastY: y, before: state(), moved: false};
      if (hit) {
        notes = notes.filter(note => note.id !== hit.note.id);
        selected.delete(hit.note.id);
        dragging.moved = true;
      }
      rightClickHandled = true;
      if (event.isTrusted) canvas.setPointerCapture(event.pointerId);
      updateControls(); needsDraw = true;
      return;
    }
    if (event.button !== 0) return;
    rightClickHandled = false;
    event.preventDefault();
    canvas.focus({preventScroll: true});
    const {x, y} = point(event), hit = hitTest(x, y);
    const additive = event.shiftKey || event.ctrlKey || event.metaKey;
    const before = state();
    if (hit) {
      if (additive) {
        if (selected.has(hit.note.id)) selected.delete(hit.note.id);
        else selected.add(hit.note.id);
        updateControls(); needsDraw = true;
        return;
      }
      if (!selected.has(hit.note.id)) selected = new Set([hit.note.id]);
      dragging = {mode: hit.resize ? 'resize' : 'move', startX: x, startY: y,
        originals: clone(notes.filter(n => selected.has(n.id))), anchor: clone(hit.note), before, moved: false};
    } else if (['paint', 'curve'].includes($('tool').value) && !additive) {
      const time = x / pixelsPerSecond - offset(), step = snapSeconds();
      if (time < 0 || y < 0 || y >= rows * rowHeight) return;
      const curve = $('tool').value === 'curve';
      const onset = !curve && step ? Math.floor(time / step + 1e-9) * step : time;
      const pitch = curve ? freePitch(y) : placedPitch(y);
      const note = {id: crypto.randomUUID(), time: onset, length: noteLength(), pitch, endPitch: pitch, curvePoints: []};
      if (curve) note.curvePoints = [{position: 0, pitch}, {position: 1, pitch}];
      notes.push(note); selected = new Set([note.id]);
      dragging = {mode: curve ? 'curve' : 'paint', startX: x, startY: y, anchorTime: onset, note, before, moved: true,
        raw: curve ? [{time: onset, pitch}] : null};
    } else {
      // Wait for release to distinguish adding a note from drawing a selection box.
      dragging = {mode: 'pending', startX: x, startY: y, x, y, additive,
        base: additive ? new Set(selected) : new Set(), before};
    }
    // Programmatic events have no active native pointer to capture.
    if (event.isTrusted) canvas.setPointerCapture(event.pointerId);
    updateControls(); needsDraw = true;
  });
  window.addEventListener('pointermove', event => {
    if (busy) return;
    if (!dragging) {
      if (event.target === canvas) {
        const {x, y} = point(event), hit = hitTest(x, y);
        canvas.style.cursor = hit ? (hit.resize ? 'ew-resize' : 'grab') : 'crosshair';
      }
      return;
    }
    const events = dragging.mode === 'curve' && event.isTrusted ? event.getCoalescedEvents?.() : null;
    for (const item of events?.length ? events : [event]) updateDrag(point(item));
  });
  function updateDrag({x, y}) {
    const d = dragging;
    if (!d) return;
    if (d.mode === 'erase') {
      if (x !== d.lastX || y !== d.lastY) eraseSegment(d.lastX, d.lastY, x, y);
      d.lastX = x; d.lastY = y;
      updateControls(); needsDraw = true;
      return;
    }
    if (d.mode === 'paint') {
      if (Math.abs(x - d.startX) >= 4) d.activated = true;
      if (d.activated) {
        const end = Math.max(0, snapTime(x / pixelsPerSecond - offset()));
        d.note.time = Math.min(d.anchorTime, end);
        d.note.length = Math.max(snapSeconds() || .03, Math.abs(end - d.anchorTime));
      }
      updateControls(); needsDraw = true;
      return;
    }
    if (d.mode === 'curve') {
      if (Math.hypot(x - d.startX, y - d.startY) >= 4) d.activated = true;
      if (d.activated) {
        const time = Math.max(0, x / pixelsPerSecond - offset()), pitch = freePitch(y);
        if (Math.abs(time - d.anchorTime) > .001) {
          const direction = time > d.anchorTime ? 1 : -1;
          if (d.direction && direction !== d.direction) d.raw = d.raw.slice(0, 1);
          d.direction = direction;
          // Backtracking redraws the tail, rather than creating two mouse positions at one time.
          while (d.raw.length > 1 && (time - d.raw[d.raw.length - 1].time) * direction <= .001) d.raw.pop();
          d.raw.push({time, pitch});
          if (d.raw.length > 256) d.raw = d.raw.filter((_, i, all) => i % 2 === 0 || i === all.length - 1);
          const points = [...d.raw].sort((a, b) => a.time - b.time);
          d.note.time = points[0].time;
          const span = points[points.length - 1].time - d.note.time;
          d.note.length = Math.max(.03, span);
          d.note.pitch = points[0].pitch; d.note.endPitch = points[points.length - 1].pitch;
          d.note.curvePoints = points.map(p => ({position: (p.time - d.note.time) / d.note.length, pitch: p.pitch}));
          d.note.curvePoints[d.note.curvePoints.length - 1].position = 1;
        }
      }
      updateControls(); needsDraw = true;
      return;
    }
    if (d.mode === 'pending' && Math.hypot(x - d.startX, y - d.startY) >= 4) d.mode = 'marquee';
    if (d.mode === 'marquee') {
      d.x = x; d.y = y;
      const left = Math.min(d.startX, x), right = Math.max(d.startX, x);
      const top = Math.min(d.startY, y), bottom = Math.max(d.startY, y);
      selected = new Set(d.base);
      for (const note of notes) {
        if (hasPath(note)) {
          const line = polyline(note);
          if (line.slice(1).some((p, i) => segmentRect(line[i], p, left - 6, top - 6, right + 6, bottom + 6))) selected.add(note.id);
          continue;
        }
        const nx = (note.time + offset()) * pixelsPerSecond, ny = (maxPitch - note.pitch) * rowHeight + 3;
        if (nx <= right && nx + Math.max(9, note.length * pixelsPerSecond) >= left && ny <= bottom && ny + rowHeight - 6 >= top)
          selected.add(note.id);
      }
      updateControls();
    } else if (d.mode === 'move' || d.mode === 'resize') {
      if (!d.activated && Math.hypot(x - d.startX, y - d.startY) < 4) return;
      d.activated = true;
      const originals = d.originals, delta = (x - d.startX) / pixelsPerSecond;
      const horizontal = Math.abs(x - d.startX) >= 4;
      const timeDelta = horizontal ? Math.max(-Math.min(...originals.map(n => n.time)), snapTime(d.anchor.time + delta) - d.anchor.time) : 0;
      const low = Math.min(...originals.flatMap(n => pathPoints(n).map(p => p.pitch)));
      const high = Math.max(...originals.flatMap(n => pathPoints(n).map(p => p.pitch)));
      const vertical = (d.startY - y) / rowHeight;
      const pitchDelta = clamp($('grid').checked ? Math.round(vertical) : vertical, minPitch - low, maxPitch - high);
      const lengthDelta = horizontal ? Math.max(.03 - Math.min(...originals.map(n => n.length)), snapTime(d.anchor.length + delta) - d.anchor.length) : 0;
      const byId = new Map(notes.map(note => [note.id, note]));
      for (const original of originals) {
        const n = byId.get(original.id);
        if (d.mode === 'resize') n.length = original.length + lengthDelta;
        else {
          n.time = original.time + timeDelta; n.pitch = original.pitch + pitchDelta; n.endPitch = original.endPitch + pitchDelta;
          n.curvePoints = (original.curvePoints || []).map(p => ({position: p.position, pitch: p.pitch + pitchDelta}));
        }
      }
      d.moved = originals.some(original => {
        const n = byId.get(original.id);
        return n.time !== original.time || n.length !== original.length || n.pitch !== original.pitch || n.endPitch !== original.endPitch;
      });
    }
    needsDraw = true;
  }
  function eraseSegment(x1, y1, x2, y2) {
    // Test the whole travelled segment so quick strokes also hit short notes between events.
    const erased = new Set();
    for (const note of notes) {
      if (hasPath(note)) {
        const line = polyline(note), a = {x: x1, y: y1}, b = {x: x2, y: y2};
        if (line.slice(1).some((p, i) => segmentsNear(a, b, line[i], p, 8))) erased.add(note.id);
        continue;
      }
      const left = (note.time + offset()) * pixelsPerSecond;
      const top = (maxPitch - note.pitch) * rowHeight + 3;
      const right = left + Math.max(9, note.length * pixelsPerSecond), bottom = top + rowHeight - 6;
      if (segmentRect({x: x1, y: y1}, {x: x2, y: y2}, left, top, right, bottom)) erased.add(note.id);
    }
    if (!erased.size) return;
    notes = notes.filter(note => !erased.has(note.id));
    for (const id of erased) selected.delete(id);
    dragging.moved = true;
  }
  function endDrag(addNote = false) {
    if (dragging?.mode === 'curve' && dragging.raw.length > 2) smoothCurve(dragging.note);
    if (dragging?.mode === 'pending' && addNote) {
      const d = dragging;
      if (d.additive) selected = new Set(d.base);
      else {
        remember();
        const pitch = placedPitch(d.startY);
        const note = {id: crypto.randomUUID(), time: Math.max(0, snapTime(d.startX / pixelsPerSecond - offset())),
          length: noteLength(), pitch, endPitch: pitch};
        notes.push(note); selected = new Set([note.id]); markDirty(); resize();
      }
    }
    if (dragging?.moved) {
      undoStack.push(dragging.before); if (undoStack.length > 100) undoStack.shift();
      redoStack = []; markDirty(); resize();
    }
    if (dragging?.mode === 'erase') rightClickHandled = true;
    dragging = null;
    canvas.style.cursor = 'crosshair';
    updateControls(); needsDraw = true;
  }
  function cancelDrag() {
    if (!dragging) return;
    if (dragging.mode === 'erase') rightClickHandled = true;
    notes = clone(dragging.before.notes);
    selected = new Set(dragging.before.selected);
    dragging = null;
    canvas.style.cursor = 'crosshair';
    resize(); updateControls(); needsDraw = true;
  }
  window.addEventListener('pointerup', event => {
    if (!dragging) return;
    updateDrag(point(event)); endDrag(true);
  });
  canvas.addEventListener('pointercancel', cancelDrag);
  canvas.addEventListener('lostpointercapture', event => {
    // WebView2 can drop capture during a held gesture. Window-level events still finish the stroke.
    if (dragging && (event.buttons & (dragging.mode === 'erase' ? 2 : 1))) return;
    cancelDrag();
  });
  window.addEventListener('blur', cancelDrag);
  function eraseNoteAt({x, y}) {
    endDrag();
    const hit = hitTest(x, y);
    if (!hit) return;
    remember();
    notes = notes.filter(note => note.id !== hit.note.id);
    selected.delete(hit.note.id);
    markDirty(); resize();
  }
  window.addEventListener('contextmenu', event => {
    if (event.target !== canvas && dragging?.mode !== 'erase' && !rightClickHandled) return;
    event.preventDefault();
    if (!projectKey || busy) return;
    // Erase strokes own the right button, including systems that show a menu on press.
    if (dragging?.mode === 'erase' || rightClickHandled) { rightClickHandled = false; return; }
    eraseNoteAt(point(event));
  });
  async function togglePlay() {
    if (playBtn.disabled) return;
    if (!audio.paused) { audio.pause(); return; }
    try { await audio.play(); status('Playing audio'); }
    catch (error) { status('Playback failed: ' + error.message + '. Reimport the file or convert it to OGG.', true); }
  }
  playBtn.onclick = togglePlay;
  function stopPlayback() { audio.pause(); if (audio.readyState >= 1) audio.currentTime = 0; needsDraw = true; }
  $('stop').onclick = stopPlayback;
  $('save').onclick = requestSave;
  $('undo').onclick = undo; $('redo').onclick = redo; $('delete').onclick = deleteSelected;
  $('grid').onchange = () => { endDrag(); remember(); undoStack[undoStack.length - 1].showGrid = !$('grid').checked; markDirty(); };
  $('singleLine').onclick = () => {
    if (busy || !overlapCount()) return;
    endDrag(); remember();
    const result = [];
    for (const note of [...notes].sort((a, b) => a.time - b.time || a.pitch - b.pitch)) {
      const last = result[result.length - 1];
      if (last && note.time === last.time) continue;
      if (last && last.time + last.length > note.time) shortenNote(last, note.time - last.time);
      result.push(note);
    }
    notes = result; selected = new Set([...selected].filter(id => notes.some(n => n.id === id)));
    markDirty(); resize();
  };
  $('noteLength').onchange = () => $('noteLength').value = noteLength();
  $('applyLength').onclick = () => {
    if (busy || !selected.size) return;
    endDrag();
    const length = noteLength();
    if (!notes.some(note => selected.has(note.id) && note.length !== length)) return;
    remember();
    for (const note of notes) if (selected.has(note.id)) note.length = length;
    markDirty(); resize();
  };
  $('volume').oninput = () => audio.volume = Number($('volume').value) / 100;
  $('zoom').oninput = () => {
    const center = (viewport.scrollLeft + viewport.clientWidth / 2) / pixelsPerSecond;
    pixelsPerSecond = Number($('zoom').value); resize();
    viewport.scrollLeft = Math.max(0, center * pixelsPerSecond - viewport.clientWidth / 2);
  };
  wave.addEventListener('pointerdown', event => {
    if (!duration || busy || audio.readyState < 1) return;
    const rect = wave.getBoundingClientRect();
    audio.currentTime = clamp((event.clientX - rect.left) / rect.width * duration, 0, duration);
    viewport.scrollLeft = Math.max(0, audio.currentTime * pixelsPerSecond - viewport.clientWidth / 3);
    needsDraw = true;
  });
  audio.addEventListener('loadedmetadata', () => {
    duration = Number.isFinite(audio.duration) ? audio.duration : 0;
    status('Audio ready · ' + format(duration), false, true); resize(); updateControls();
  });
  audio.addEventListener('error', () => {
    if (!audio.getAttribute('src')) return;
    const errors = {1: 'Audio loading was interrupted.', 2: 'The audio file could not be read.', 3: 'The audio file could not be decoded.', 4: 'The audio format is unsupported or the file is missing.'};
    status((errors[audio.error?.code] || 'Audio could not be loaded.') + ' Try importing MP3, WAV, or OGG.', true);
    updateControls();
  });
  audio.addEventListener('play', () => { playBtn.textContent = 'Pause'; needsDraw = true; });
  audio.addEventListener('pause', () => { playBtn.textContent = 'Play'; needsDraw = true; });
  audio.addEventListener('ended', () => { playBtn.textContent = 'Play'; status('Playback finished'); });
  audio.addEventListener('timeupdate', () => needsDraw = true);
  for (const input of [bpmInput, offsetInput]) {
    input.addEventListener('focus', () => previousSettings = JSON.stringify({bpm: bpm(), offset: offset()}));
    input.addEventListener('change', () => {
      const before = JSON.parse(previousSettings || JSON.stringify({bpm: 120, offset: 0}));
      if (!Number.isFinite(Number(input.value))) input.value = input === bpmInput ? 120 : 0;
      bpmInput.value = bpm();
      if (before.bpm === bpm() && before.offset === offset()) return;
      undoStack.push({...state(), ...before}); redoStack = [];
      previousSettings = JSON.stringify({bpm: bpm(), offset: offset()});
      resize(); markDirty();
    });
  }
  viewport.addEventListener('scroll', () => needsDraw = true);
  window.addEventListener('resize', resize);
  document.addEventListener('keydown', event => {
    if (busy) return;
    const control = event.ctrlKey || event.metaKey, key = event.key.toLowerCase();
    if (control && (key === 'n' || key === 'o')) {
      event.preventDefault(); post({type: 'studioCommand', command: key === 'n' ? 'New' : 'Open'}); return;
    }
    if (!projectKey) return;
    const editable = event.target instanceof Element && event.target.matches('input,select,textarea');
    if (control && key === 's') {
      event.preventDefault();
      if (event.shiftKey || event.altKey) post({type: 'studioCommand', command: event.shiftKey ? 'SaveProjectAs' : 'SaveProject'});
      else requestSave();
    }
    else if (window.msmPreview?.getState().mode !== 'editor' && window.msmPreview) {
      if (!editable && event.code === 'Space' && event.target.tagName !== 'BUTTON') { event.preventDefault(); togglePlay(); }
      return;
    }
    else if (!editable && (event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'a') {
      event.preventDefault(); selectAll();
    }
    else if (!editable && event.key === 'Escape') { event.preventDefault(); clearSelection(); }
    else if (!editable && (event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'z') { event.preventDefault(); event.shiftKey ? redo() : undo(); }
    else if (!editable && (event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'y') { event.preventDefault(); redo(); }
    else if (!editable && (event.key === 'Delete' || event.key === 'Backspace')) { event.preventDefault(); deleteSelected(); }
    else if (!editable && event.code === 'Space' && event.target.tagName !== 'BUTTON') { event.preventDefault(); togglePlay(); }
  });
  function getSnapshot() { endDrag(); return {type: 'saveChart', projectKey, bpm: bpm(), offset: offset(), showGrid: $('grid').checked, notes: clone(notes), revision}; }
  function requestSave() { if (projectKey && !busy) post(getSnapshot()); }
  window.msmEditor = {requestSave, getSnapshot, undo, redo, deleteSelected, selectAll, clearSelection, togglePlay, stopPlayback, isInteracting: () => !!dragging,
    getPreviewState: () => ({notes, offset: offset(), duration, projectKey, busy}), finishInteraction: endDrag, refreshControls: updateControls};
  window.chrome?.webview?.addEventListener('message', event => {
    const message = event.data;
    if (message.type === 'busy') { busy = message.busy; updateControls(); return; }
    if (message.type === 'saved' && message.projectKey === projectKey) {
      if (message.revision == null || message.revision === revision) { dirty = false; $('save').textContent = 'Save chart…'; }
      return;
    }
    if (message.type === 'loadAudio' && message.projectKey === projectKey) { loadAudio(message.audioUrl); return; }
    if (message.projectKey === projectKey && message.type === 'recordProjectEdit') {
      undoStack.push({projectEdit: message.id, label: message.label});
      if (undoStack.length > 100) undoStack.shift();
      redoStack = []; updateControls(); return;
    }
    if (message.projectKey === projectKey && message.type === 'cancelProjectHistory') {
      const from = message.direction === 'undo' ? redoStack : undoStack;
      const to = message.direction === 'undo' ? undoStack : redoStack;
      if (from.at(-1)?.projectEdit === message.id) to.push(from.pop());
      busy = false; updateControls(); return;
    }
    if (message.type !== 'loadProject') return;
    projectKey = message.projectKey;
    bpmInput.value = message.bpm ?? 120; offsetInput.value = message.offset ?? 0;
    $('grid').checked = message.showGrid ?? true;
    audioOnsets = message.audioOnsets || [];
    notes = (message.notes || []).map(n => ({
      id: n.Id || n.id || crypto.randomUUID(), time: n.Time ?? n.time ?? 0, length: n.Length ?? n.length ?? .25,
      pitch: n.Pitch ?? n.pitch ?? 60, endPitch: n.EndPitch ?? n.endPitch ?? n.Pitch ?? n.pitch ?? 60,
      curvePoints: (n.CurvePoints ?? n.curvePoints ?? []).map(p => ({position: p.Position ?? p.position, pitch: p.Pitch ?? p.pitch}))
    }));
    selected = new Set(); dragging = null;
    if (!message.keepHistory) { undoStack = []; redoStack = []; }
    dirty = false; revision = 0; rightClickHandled = false;
    $('save').textContent = 'Save chart…';
    previousSettings = JSON.stringify({bpm: bpm(), offset: offset()});
    viewport.scrollTop = 0;
    loadAudio(message.audioUrl);
    if (notes.length) viewport.scrollLeft = Math.max(0, (Math.min(...notes.map(n => n.time)) + offset()) * pixelsPerSecond - 60);
  });
  function frame(now) {
    if (now - lastFrame > 30 && (needsDraw || !audio.paused)) {
      $('time').textContent = format(audio.currentTime) + ' / ' + format(duration);
      $('wavePlayhead').style.left = (duration ? audio.currentTime / duration * 100 : 0) + '%';
      if (!audio.paused && $('follow').checked && !dragging) {
        const x = audio.currentTime * pixelsPerSecond - viewport.scrollLeft;
        if (x > viewport.clientWidth * .85 || x < 0) viewport.scrollLeft = Math.max(0, audio.currentTime * pixelsPerSecond - viewport.clientWidth * .2);
      }
      draw(); needsDraw = false; lastFrame = now;
    }
    requestAnimationFrame(frame);
  }
  resize(); updateControls(); requestAnimationFrame(frame);
  post({type: 'editorReady'});
})();
