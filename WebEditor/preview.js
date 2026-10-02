(() => {
  'use strict';
  const $ = id => document.getElementById(id), editor = window.msmEditor;
  const audio = $('audio'), canvas = $('previewCanvas'), ctx = canvas.getContext('2d');
  let mode = 'editor', pointerPitch = 60, held = false, lastTime = audio.currentTime, key = '', totals = new Map(), combo = 0, best = 0;
  let hitSeconds = 0, totalSeconds = 0;
  const clamp = (v,a,b) => Math.max(a,Math.min(b,v));
  let toneContext, oscillator, toneGain, analyser, toneError = '', toneFrequency = 0, toneLevel = 0;
  const pitchFrequency = pitch => 440 * Math.pow(2,(pitch-69)/12);
  const instruments = {
    soft:{type:'triangle',gain:.22}, flute:{type:'sine',gain:.22},
    brass:{harmonics:[0,1,.55,.3,.2,.12,.08],gain:.18},
    organ:{harmonics:[0,1,.45,.25,.12,0,.08],gain:.2},
    chip:{type:'square',gain:.12}, none:{gain:0}
  };
  let instrument='soft';
  try { const saved=localStorage.getItem('msm-play-test-instrument'); if(Object.hasOwn(instruments,saved))instrument=saved; } catch {}
  $('testInstrument').value=instrument;
  function applyInstrument() {
    if(!oscillator || instrument==='none')return;
    const preset=instruments[instrument];
    if(preset.harmonics) oscillator.setPeriodicWave(toneContext.createPeriodicWave(new Float32Array(preset.harmonics.length),new Float32Array(preset.harmonics)));
    else oscillator.type=preset.type;
  }
  function setInstrument(value) {
    if(!Object.hasOwn(instruments,value))return;
    instrument=value; $('testInstrument').value=value;
    try {localStorage.setItem('msm-play-test-instrument',value);} catch {}
    // Change only the practice voice; playback, chart data and scoring keep their state.
    applyInstrument(); if(held)ensureTone(); updateTone();
  }
  $('testInstrument').onchange=()=>setInstrument($('testInstrument').value);
  function ensureTone() {
    if(mode!=='test' || instrument==='none')return;
    try {
      if(!toneContext) {
        toneContext = new AudioContext({latencyHint:'interactive'});
        oscillator = toneContext.createOscillator(); toneGain = toneContext.createGain(); analyser = toneContext.createAnalyser();
        // A soft, harmonic instrument tone; one continuous voice lets slides remain smooth.
        applyInstrument(); toneGain.gain.value = 0; analyser.fftSize = 2048;
        oscillator.connect(toneGain); toneGain.connect(analyser); analyser.connect(toneContext.destination); oscillator.start();
      }
      if(toneContext.state==='suspended') toneContext.resume().catch(()=>{toneError='Click the play area to enable the note sound';});
    } catch { toneError='Note sound unavailable on this device'; }
  }
  function updateTone() {
    const state=editor.getPreviewState();
    const sounding=mode==='test' && held && !audio.paused && !audio.ended && !audio.seeking && !state.busy && !!state.projectKey && !document.hidden;
    const frequency=pitchFrequency(pointerPitch), level=sounding && !audio.muted ? audio.volume*instruments[instrument].gain : 0;
    if(!toneContext)return;
    const now=toneContext.currentTime;
    if(frequency!==toneFrequency) {
      oscillator.frequency.cancelScheduledValues(now); oscillator.frequency.setTargetAtTime(frequency,now,.008); toneFrequency=frequency;
    }
    if(level!==toneLevel) {
      toneGain.gain.cancelScheduledValues(now); toneGain.gain.setTargetAtTime(level,now,.006); toneLevel=level;
    }
  }
  function release() { held=false; updateTone(); }
  function movePointer(e) {
    const rect=canvas.getBoundingClientRect(); pointerPitch=clamp(73-(e.clientY-rect.top-30)/(rect.height-60)*26,47,73); updateTone();
  }
  function points(n) { return n.curvePoints?.length >= 2 ? n.curvePoints : [{position:0,pitch:n.pitch},{position:1,pitch:n.endPitch}]; }
  function pitchAt(n, time) {
    const p = clamp((time-n.time)/n.length,0,1), path = points(n);
    for(let i=1;i<path.length;i++) if(p <= path[i].position) {
      const a=path[i-1],b=path[i],r=(p-a.position)/(b.position-a.position);
      return a.pitch+(b.pitch-a.pitch)*r;
    }
    return path.at(-1).pitch;
  }
  function reset() { release(); totals=new Map(); combo=best=0; hitSeconds=totalSeconds=0; lastTime=audio.currentTime; }
  function setMode(value) {
    if(!['editor','preview','test'].includes(value)) return;
    editor.finishInteraction(); audio.pause(); mode=value; $('viewMode').value=value; document.body.dataset.view=value;
    $('previewViewport').hidden=value==='editor'; $('previewTitle').textContent=value==='test'?'Play Test':'Preview · automatic follow';
    reset();
    $('testInstrument').disabled=value!=='test';
    editor.refreshControls();
    // Leave the timeline and chart untouched when moving between views.
    if(value!=='editor') canvas.focus();
    else window.dispatchEvent(new Event('resize'));
  }
  $('viewMode').onchange=()=>setMode($('viewMode').value);
  $('restartTest').onclick=()=>{ editor.stopPlayback(); reset(); editor.togglePlay(); };
  canvas.addEventListener('pointermove',movePointer);
  canvas.addEventListener('pointerdown',e=>{ if(e.button!==0)return; movePointer(e); held=true; ensureTone(); updateTone(); canvas.focus(); if(e.isTrusted)canvas.setPointerCapture(e.pointerId); e.preventDefault(); });
  window.addEventListener('pointerup',release);
  canvas.addEventListener('pointercancel',release);
  canvas.addEventListener('lostpointercapture',release);
  canvas.addEventListener('contextmenu',e=>e.preventDefault());
  window.addEventListener('blur',release);
  document.addEventListener('visibilitychange',()=>{if(document.hidden)release();});
  document.addEventListener('keydown',e=>{
    if(mode==='test' && e.key.toLowerCase()==='z' && !e.ctrlKey && !e.metaKey && !e.target.matches('input,select,textarea')) { held=true; ensureTone(); updateTone(); e.preventDefault(); }
  });
  document.addEventListener('keyup',e=>{ if(e.key.toLowerCase()==='z')release(); });
  audio.addEventListener('play',()=>{ensureTone();updateTone();});
  for(const event of ['pause','ended','error','emptied']) audio.addEventListener(event,release);
  audio.addEventListener('volumechange',updateTone);
  audio.addEventListener('seeking',reset);
  $('tolerance').onchange=reset;
  function judge(time,state) {
    if(mode!=='test' || state.busy) { lastTime=time; return; }
    if(time<lastTime || time-lastTime>.3) { reset(); lastTime=time; return; }
    if(time===lastTime)return;
    const delta=time-lastTime;
    for(const n of state.notes) {
      const start=n.time+state.offset, end=start+n.length;
      const a=Math.max(lastTime,start,0),b=Math.min(time,end);
      if(b<=a)continue;
      const progress=totals.get(n.id)||{hit:0,total:0,complete:false};
      // Integrate using the audio clock; pause, rendering speed and note duration cannot move notes ahead of sound.
      const steps=Math.max(1,Math.ceil((b-a)/.01)),step=(b-a)/steps;
      for(let i=0;i<steps;i++) {
        const t=a+(i+.5)*step, target=pitchAt(n,t-state.offset);
        progress.total+=step; totalSeconds+=step;
        if(held && Math.abs(pointerPitch-target)<=Number($('tolerance').value)) { progress.hit+=step; hitSeconds+=step; }
      }
      totals.set(n.id,progress);
    }
    for(const n of state.notes) {
      const result=totals.get(n.id);
      if(result && !result.complete && time>=n.time+state.offset+n.length) {
        result.complete=true; combo=result.hit/Math.max(result.total,.001)>=.6?combo+1:0; best=Math.max(best,combo);
      }
    }
    lastTime=time;
  }
  function draw() {
    if(mode==='editor')return;
    const state=editor.getPreviewState();
    if(state.projectKey!==key) { key=state.projectKey; reset(); }
    const rect=canvas.getBoundingClientRect(),ratio=devicePixelRatio||1,w=rect.width,h=rect.height;
    if(canvas.width!==Math.round(w*ratio)||canvas.height!==Math.round(h*ratio)) { canvas.width=Math.round(w*ratio); canvas.height=Math.round(h*ratio); }
    ctx.setTransform(ratio,0,0,ratio,0,0); ctx.fillStyle='#101724';ctx.fillRect(0,0,w,h);
    const time=audio.currentTime,hitX=Math.max(65,w*.18),speed=Math.max(100,(w-hitX)/4),y=p=>30+(73-p)/26*(h-60);
    judge(time,state); updateTone();
    ctx.font='12px Segoe UI';
    for(const pitch of [47,53,60,66,73]) {ctx.fillStyle='#7385a2';ctx.fillText(String(pitch),8,y(pitch)+4);ctx.strokeStyle='#202d42';ctx.beginPath();ctx.moveTo(35,y(pitch));ctx.lineTo(w,y(pitch));ctx.stroke();}
    ctx.strokeStyle='#fff';ctx.lineWidth=2;ctx.beginPath();ctx.moveTo(hitX,10);ctx.lineTo(hitX,h-10);ctx.stroke();
    let active=null;
    for(const n of state.notes) {
      const start=n.time+state.offset,end=start+n.length;
      if(end<time-.7||start>time+5)continue;
      if(time>=start && time<end && !active)active=n;
      const current=time>=start&&time<end;
      ctx.strokeStyle=current?'#78e9c1':'#8dabff';ctx.lineWidth=10;ctx.lineCap='round';ctx.lineJoin='round';ctx.beginPath();
      points(n).forEach((p,i)=>{const x=hitX+(start+p.position*n.length-time)*speed; i?ctx.lineTo(x,y(p.pitch)):ctx.moveTo(x,y(p.pitch));});ctx.stroke();
      ctx.fillStyle='#e3ecff';ctx.beginPath();ctx.arc(hitX+(start-time)*speed,y(n.pitch),5,0,Math.PI*2);ctx.fill();
    }
    const pitch=mode==='preview'&&active?pitchAt(active,time-state.offset):pointerPitch;
    const matched=active && Math.abs(pitch-pitchAt(active,time-state.offset))<=Number($('tolerance').value);
    ctx.fillStyle=mode==='preview'?'#f5cd70':held?(matched?'#63f0bc':'#ff947f'):'#e4edff';ctx.beginPath();ctx.arc(hitX,y(pitch),9,0,Math.PI*2);ctx.fill();
    const accuracy=totalSeconds?Math.round(hitSeconds/totalSeconds*100):0;
    $('previewScore').textContent=mode==='test'?`Accuracy ${accuracy}% · Combo ${combo} · Best ${best} · ${held?'Holding':'Hold left click or Z'}${audio.ended?' · Finished':''}${toneError?' · '+toneError:''}`:'Follows the current audio and chart automatically · switch to Play Test to practice';
    $('viewMode').disabled=state.busy||!state.projectKey;
  }
  function frame() {draw();requestAnimationFrame(frame);} requestAnimationFrame(frame);
  window.msmPreview={setMode,reset,pitchAt,setInstrument,getState:()=>({mode,held,pointerPitch,hitSeconds,totalSeconds,combo,best,instrument}),
    setInput:(pitch,down)=>{pointerPitch=clamp(pitch,47,73);held=!!down;ensureTone();updateTone();}, judge:(time)=>judge(time,editor.getPreviewState()),
    getToneState:()=>{const samples=new Float32Array(analyser?.fftSize||0); analyser?.getFloatTimeDomainData(samples);
      return {context:toneContext?.state||'uninitialized',frequency:oscillator?.frequency.value||0,level:toneGain?.gain.value||0,instrument,waveform:oscillator?.type||'',
        rms:samples.length?Math.sqrt(samples.reduce((sum,v)=>sum+v*v,0)/samples.length):0};}};
})();
