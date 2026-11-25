namespace DevToolsMcp.Server.Core.Settings;

/// <summary>Self-contained settings page (no external assets; rendered from /api/modules).</summary>
internal static class SettingsPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>devtools-mcp settings</title>
<style>
:root{--bg:#f6f7f9;--card:#fff;--fg:#1d2330;--muted:#5f6b7a;--line:#dde2e8;--accent:#2563eb;--ok:#15803d;--bad:#b91c1c;color-scheme:light dark}
@media (prefers-color-scheme:dark){:root{--bg:#111418;--card:#1a1f26;--fg:#e6e9ee;--muted:#9aa5b1;--line:#2c333d;--accent:#60a5fa;--ok:#4ade80;--bad:#f87171}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.5 system-ui,-apple-system,Segoe UI,sans-serif}
main{max-width:640px;margin:0 auto;padding:24px 16px}h1{font-size:22px;margin:0 0 4px}p.sub{color:var(--muted);margin:0 0 16px}
section{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:16px 20px;margin-bottom:16px}
.tabs{display:flex;gap:4px;margin-bottom:-1px;position:relative;overflow-x:auto}
.tab{display:flex;align-items:center;gap:8px;padding:8px 14px;border:1px solid transparent;border-bottom:none;border-radius:8px 8px 0 0;background:transparent;color:var(--muted);font-weight:600}
.tab:hover{color:var(--fg)}.tab[aria-selected=true]{background:var(--card);border-color:var(--line);color:var(--fg)}.tab:focus-visible{outline:2px solid var(--accent);outline-offset:-2px}
#modules>section{border-top-left-radius:0}#modules .status{margin-bottom:4px}
h2{font-size:17px;margin:0 0 4px;display:flex;align-items:center;gap:8px}.dot{width:9px;height:9px;border-radius:50%;background:var(--bad)}.dot.on{background:var(--ok)}
.status{color:var(--muted);font-size:13px;margin:0 0 16px}label{display:block;font-weight:600;font-size:13px;margin:10px 0 4px}
input,select{width:100%;padding:8px 10px;border:1px solid var(--line);border-radius:6px;background:var(--bg);color:var(--fg);font:inherit}
.help{color:var(--muted);font-size:12px;margin-top:3px}.row{display:flex;gap:8px;margin-top:16px;align-items:center;flex-wrap:wrap}
button{padding:8px 14px;border-radius:6px;border:1px solid var(--accent);background:var(--accent);color:#fff;font:inherit;cursor:pointer}
button.ghost{background:transparent;color:var(--accent)}.msg{font-size:13px}.msg.ok{color:var(--ok)}.msg.bad{color:var(--bad)}
code,pre{font:12.5px ui-monospace,Menlo,Consolas,monospace}pre{background:var(--bg);border:1px solid var(--line);border-radius:6px;padding:10px;overflow:auto}
</style>
</head>
<body>
<main>
<h1>devtools-mcp</h1>
<p class="sub">Local MCP server. Tools of unconfigured modules are hidden, so they cost no tokens.</p>
<div class="tabs" role="tablist" id="tabs" aria-label="Providers"></div>
<div id="modules"></div>
<section><h2>Connect your client</h2>
<p class="status">VS Code / GitHub Copilot <code>.vscode/mcp.json</code> (reload the MCP server after changing settings):</p>
<pre id="client"></pre><p class="help" id="file"></p></section>
</main>
<script>
const $=(t,a={},...c)=>{const e=document.createElement(t);for(const[k,v]of Object.entries(a))k in e?e[k]=v:e.setAttribute(k,v);e.append(...c);return e};
async function call(method,url,body){
  const h={'Content-Type':'application/json'};const key=sessionStorage.getItem('mcpKey');if(key)h['X-API-Key']=key;
  const r=await fetch(url,{method,headers:h,body:body?JSON.stringify(body):undefined});
  if(r.status===401){const k=prompt('API key (McpAuth:ApiKey)');if(k){sessionStorage.setItem('mcpKey',k);return call(method,url,body)}}
  const data=await r.json().catch(()=>({ok:false,message:'HTTP '+r.status}));return{ok:r.ok,data};
}
function render(m){
  const msg=$('span',{className:'msg'});const form=$('form');
  for(const s of m.settings){
    const id=m.name+'-'+s.key;let input;
    if(s.kind==='select'){input=$('select',{id,name:s.key});for(const o of s.options)input.append($('option',{value:o,selected:(s.value||s.options[0])===o},o))}
    else input=$('input',{id,name:s.key,type:s.kind==='secret'?'password':s.kind==='url'?'url':s.kind==='email'?'email':'text',value:s.value||'',
      placeholder:s.kind==='secret'&&s.hasValue?'•••••••• saved (leave empty to keep)':'',autocomplete:'off'});
    form.append($('label',{htmlFor:id},s.label+(s.required?' *':'')),input);if(s.help)form.append($('div',{className:'help'},s.help));
  }
  const test=$('button',{type:'button',className:'ghost'},'Test connection');
  form.append($('div',{className:'row'},$('button',{type:'submit'},'Save'),test,msg));
  const sec=$('section',{id:'panel-'+m.name,role:'tabpanel','aria-labelledby':'tab-'+m.name},
    $('p',{className:'status'},(m.active?'Active · ':'Inactive · ')+m.status),form);
  sec.show=(ok,text)=>{msg.className='msg '+(ok?'ok':'bad');msg.textContent=text};
  const runTest=async(target,prefix)=>{target.show(true,prefix+'Testing…');const t=await call('POST','/api/modules/'+m.name+'/test');target.show(t.data.ok,prefix+t.data.message)};
  form.onsubmit=async e=>{e.preventDefault();
    const r=await call('PUT','/api/modules/'+m.name,Object.fromEntries(new FormData(form)));
    if(!r.ok)return sec.show(false,r.data.message);
    const fresh=render(r.data);sec.replaceWith(fresh);tabs[m.name].mark(r.data);await runTest(fresh,'Saved. ')};
  test.onclick=()=>runTest(sec,'');
  return sec;
}
const tabs={};
function select(name,focus){
  for(const[n,t]of Object.entries(tabs)){const on=n===name;t.setAttribute('aria-selected',on);t.tabIndex=on?0:-1;
    const p=document.getElementById('panel-'+n);if(p)p.hidden=!on;if(on&&focus)t.focus()}
  try{sessionStorage.setItem('mcpTab',name)}catch{}
}
function tab(m){
  const dot=$('span',{className:'dot'});
  const t=$('button',{type:'button',className:'tab',id:'tab-'+m.name,role:'tab','aria-controls':'panel-'+m.name},dot,m.title);
  t.mark=v=>{dot.className='dot'+(v.active?' on':'');t.title=v.active?'Configured':'Not configured'};t.mark(m);
  t.onclick=()=>select(m.name);
  t.onkeydown=e=>{const names=Object.keys(tabs),i=names.indexOf(m.name);
    const j=e.key==='ArrowRight'?i+1:e.key==='ArrowLeft'?i-1:e.key==='Home'?0:e.key==='End'?names.length-1:null;
    if(j===null)return;e.preventDefault();select(names[(j+names.length)%names.length],true)};
  return tabs[m.name]=t;
}
(async()=>{
  const r=await call('GET','/api/modules/');if(!r.ok){document.getElementById('modules').textContent=r.data.message;return}
  const box=document.getElementById('modules'),bar=document.getElementById('tabs');
  for(const m of r.data.modules){bar.append(tab(m));box.append(render(m))}
  let first;try{first=sessionStorage.getItem('mcpTab')}catch{}
  if(r.data.modules.length)select(tabs[first]?first:r.data.modules[0].name);
  document.getElementById('client').textContent=JSON.stringify({servers:{'devtools-mcp':{type:'http',url:r.data.mcpUrl}}},null,2);
  document.getElementById('file').textContent='Settings are stored in '+r.data.settingsFile;
})();
</script>
</body>
</html>
""";
}
