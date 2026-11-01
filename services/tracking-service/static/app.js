'use strict';
const $ = id => document.getElementById(id);
let reports = [], map, path, markers, english = false, querying = false;
async function api(path, method = 'GET', data) {
  const response = await fetch('/api/' + path, {method, credentials:'same-origin', headers:{'Content-Type':'application/json','X-Requested-With':'BLEStudio'}, ...(data !== undefined ? {body:JSON.stringify(data)} : {})});
  const value = await response.json();
  if (!response.ok) { if (response.status === 401) { $('workspace').hidden = true; $('login').hidden = false; } throw new Error(value.code || 'ServiceUnavailable'); }
  return value;
}
function message(id, error) { $(id).textContent = error.message || String(error); }
function time(value) { return new Date(value).toLocaleString(english ? 'en-GB' : 'zh-TW'); }
function td(row, value) { const cell = document.createElement('td'); cell.textContent = value; row.append(cell); }
function render() {
  $('count').textContent = reports.length; $('empty').hidden = reports.length > 0; $('map').hidden = !reports.length;
  $('source').textContent = reports[0]?.source || '—'; $('lastTime').textContent = reports[0] ? time(reports[0].observedAt) : '—';
  $('history').replaceChildren();
  for (const r of reports) { const row = document.createElement('tr'); td(row,time(r.observedAt)); td(row,r.latitude.toFixed(7)); td(row,r.longitude.toFixed(7)); td(row,r.accuracyMeters+' m'); td(row,r.source); $('history').append(row); }
  if (!reports.length) return;
  if (!window.L) { $('queryStatus').textContent = 'Map library unavailable; coordinates and history remain available.'; return; }
  if (!map) { map = L.map('map'); L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {maxZoom:19, attribution:'© <a href="https://www.openstreetmap.org/copyright">OpenStreetMap contributors</a>'}).addTo(map); markers = L.layerGroup().addTo(map); }
  markers.clearLayers(); if(path) map.removeLayer(path);
  const ordered = [...reports].reverse(); const coordinates = ordered.map(r=>[r.latitude,r.longitude]);
  path = L.polyline(coordinates,{color:'#37af98',weight:3}).addTo(map);
  for (const r of ordered) { const popup=document.createElement('span'); popup.textContent = `${time(r.observedAt)} · ${r.source} · ±${r.accuracyMeters} m`; L.circleMarker([r.latitude,r.longitude],{radius:5,color:'#147b6c',fillOpacity:.8}).bindPopup(popup).addTo(markers); }
  map.invalidateSize(); map.fitBounds(L.latLngBounds(coordinates).pad(.2),{maxZoom:16});
}
async function loadLocations(query = false) {
  const id = $('tracker').value; if(!id || querying) return; querying=true; $('refresh').disabled=true;
  try { if(query) { $('queryStatus').textContent = english ? 'Querying provider…' : '正在查詢來源…'; await api(`trackers/${id}/refresh`,'POST'); } const result=await api(`trackers/${id}/locations`); reports=result.reports; $('queryStatus').textContent=result.status; render(); }
  catch(e) { message('queryStatus',e); } finally { querying=false; $('refresh').disabled=false; }
}
async function loadTokens() {
  try { const tokens=await api('tokens'); $('tokens').replaceChildren(); for(const token of tokens) { const row=document.createElement('div'), label=document.createElement('span'), button=document.createElement('button'); label.textContent=token.name+' · '+new Date(token.expires*1000).toLocaleDateString(); button.textContent=token.revoked ? '已撤銷 / Revoked' : '撤銷 / Revoke'; button.disabled=!!token.revoked; button.onclick=async()=>{try{await api('tokens/'+token.id,'DELETE');await loadTokens();}catch(e){message('ownerStatus',e);}};row.append(label,button);$('tokens').append(row); } }
  catch(e) { if(e.message==='OwnerRequired') $('ownerTools').hidden=true; else message('ownerStatus',e); }
}
async function enter() {
  const trackers=await api('trackers'); $('login').hidden=true; $('workspace').hidden=false; $('tracker').replaceChildren();
  for(const tracker of trackers) { const option=document.createElement('option');option.value=tracker.id;option.textContent=tracker.name+' · '+tracker.protocol;$('tracker').append(option); }
  const selected=new URLSearchParams(location.hash.slice(1)).get('tracker'); if(trackers.some(t=>t.id===selected)) $('tracker').value=selected;
  await Promise.all([loadLocations(),loadTokens()]);
}
$('loginForm').onsubmit=async e=>{e.preventDefault();try{await api('login','POST',{username:'owner',password:$('password').value});$('password').value='';await enter();}catch(error){message('loginStatus',error);}};
$('logout').onclick=async()=>{try{await api('logout','POST');location.reload();}catch(e){message('queryStatus',e);}};
$('refresh').onclick=()=>loadLocations(true); $('tracker').onchange=()=>{history.replaceState(null,'','#tracker='+$('tracker').value);loadLocations();};
$('newToken').onclick=async()=>{try{const credential=await api('tokens','POST',{name:'Windows PC'});const url=URL.createObjectURL(new Blob([JSON.stringify(credential,null,2)],{type:'application/json'}));const link=document.createElement('a');link.href=url;link.download='ble-studio-desktop-credential.json';link.click();setTimeout(()=>URL.revokeObjectURL(url),1000);await loadTokens();}catch(e){message('ownerStatus',e);}};
$('googleAuth').onclick=async()=>{try{const file=$('googleFile').files[0];if(!file)throw new Error('Select your authorized Auth/secrets.json');const result=await api('providers/google','POST',JSON.parse(await file.text()));$('googleFile').value='';$('ownerStatus').textContent=result.status;}catch(e){message('ownerStatus',e);}};
$('appleAuth').onclick=async()=>{try{const result=await api('providers/apple','POST',{username:$('appleUser').value,password:$('applePassword').value});$('applePassword').value='';$('ownerStatus').textContent=result.status;}catch(e){message('ownerStatus',e);}};
$('language').onclick=()=>{english=!english;document.documentElement.lang=english?'en':'zh-Hant';document.querySelectorAll('[data-en]').forEach(el=>{if(!el.dataset.zh)el.dataset.zh=el.textContent;el.textContent=english?el.dataset.en:el.dataset.zh;});$('language').textContent=english?'繁體中文':'English';render();};
setInterval(()=>{if(!$('workspace').hidden)loadLocations();},60000);
enter().catch(()=>{});
