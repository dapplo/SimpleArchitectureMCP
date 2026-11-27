namespace SimpleArchitectureMcp.Server.Core.Settings;

/// <summary>Self-contained settings page (no external assets; rendered from /api/modules).</summary>
internal static class SettingsPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>simple-architecture-mcp settings</title>
<style>
:root{--bg:#f6f7f9;--card:#fff;--fg:#1d2330;--muted:#5f6b7a;--line:#dde2e8;--accent:#2563eb;--ok:#15803d;--bad:#b91c1c;color-scheme:light dark}
@media (prefers-color-scheme:dark){:root{--bg:#111418;--card:#1a1f26;--fg:#e6e9ee;--muted:#9aa5b1;--line:#2c333d;--accent:#60a5fa;--ok:#4ade80;--bad:#f87171}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.5 system-ui,-apple-system,Segoe UI,sans-serif}
main{max-width:640px;margin:0 auto;padding:24px 16px}h1{font-size:22px;margin:0 0 4px}p.sub{color:var(--muted);margin:0 0 16px}
section{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:16px 20px;margin-bottom:16px}
.tabs{display:flex;gap:4px;margin-bottom:-1px;position:relative;overflow-x:auto}
.tab{display:flex;align-items:center;white-space:nowrap;gap:7px;padding:8px 11px;border:1px solid transparent;border-bottom:none;border-radius:8px 8px 0 0;background:transparent;color:var(--muted);font-weight:600}
.tab:hover{color:var(--fg)}.tab[aria-selected=true]{background:var(--card);border-color:var(--line);color:var(--fg)}.tab:focus-visible{outline:2px solid var(--accent);outline-offset:-2px}
#modules>section{border-top-left-radius:0}#modules .status{margin-bottom:4px}
h2{font-size:17px;margin:0 0 4px;display:flex;align-items:center;gap:8px}.dot{width:9px;height:9px;border-radius:50%;background:var(--bad)}.dot.on{background:var(--ok)}
.status{color:var(--muted);font-size:13px;margin:0 0 16px}label{display:block;font-weight:600;font-size:13px;margin:10px 0 4px}
input,select{width:100%;padding:8px 10px;border:1px solid var(--line);border-radius:6px;background:var(--bg);color:var(--fg);font:inherit}
.help{color:var(--muted);font-size:12px;margin-top:3px}.row{display:flex;gap:8px;margin-top:16px;align-items:center;flex-wrap:wrap}
button{padding:8px 14px;border-radius:6px;border:1px solid var(--accent);background:var(--accent);color:#fff;font:inherit;cursor:pointer}
button.ghost{background:transparent;color:var(--accent)}.msg{font-size:13px}.msg.ok{color:var(--ok)}.msg.bad{color:var(--bad)}
.conv{border-top:1px solid var(--line);margin-top:16px;padding-top:4px}.conv .row{margin-top:4px;flex-wrap:nowrap}.conv select{flex:0 0 auto;width:auto;max-width:45%}.conv input{flex:1;min-width:0}
code,pre{font:12.5px ui-monospace,Menlo,Consolas,monospace}pre{background:var(--bg);border:1px solid var(--line);border-radius:6px;padding:10px;overflow:auto;margin:8px 0 0;max-height:260px;white-space:pre-wrap;overflow-wrap:anywhere}
.pick{display:flex;gap:8px}.pick select{flex:1;min-width:0}.scope{display:flex;gap:4px 14px;flex-wrap:wrap}
.scope label{display:flex;align-items:center;gap:5px;font-weight:400;font-size:13px;margin:0}.scope input{width:auto;margin:0}
ul.where{margin:4px 0 0;padding-left:18px;font-size:13px}ul.where code{word-break:break-all}.badge{color:var(--muted);font-size:12px}
p.foot{color:var(--muted);font-size:12px;margin:8px 0 0}
</style>
</head>
<body>
<main>
<h1>simple-architecture-mcp</h1>
<p class="sub">Local MCP server. Tools of unconfigured modules are hidden, so they cost no tokens.</p>
<div class="tabs" role="tablist" id="tabs" aria-label="Providers"></div>
<div id="modules"></div>
<p class="foot" id="file"></p>
</main>
<script>
const $=(t,a={},...c)=>{const e=document.createElement(t);for(const[k,v]of Object.entries(a))k in e?e[k]=v:e.setAttribute(k,v);e.append(...c);return e};
async function call(method,url,body){
  const h={'Content-Type':'application/json'};const key=sessionStorage.getItem('mcpKey');if(key)h['X-API-Key']=key;
  const r=await fetch(url,{method,headers:h,body:body?JSON.stringify(body):undefined});
  if(r.status===401){const k=prompt('API key (McpAuth:ApiKey)');if(k){sessionStorage.setItem('mcpKey',k);return call(method,url,body)}}
  const data=await r.json().catch(()=>({ok:false,message:'HTTP '+r.status}));return{ok:r.ok,data};
}
async function download(url,body){
  const h={};const key=sessionStorage.getItem('mcpKey');if(key)h['X-API-Key']=key;
  const r=await fetch(url,{method:'POST',headers:h,body});
  if(r.status===401){const k=prompt('API key (McpAuth:ApiKey)');if(k){sessionStorage.setItem('mcpKey',k);return download(url,body)}}
  if(!r.ok){const d=await r.json().catch(()=>({message:'HTTP '+r.status}));return{ok:false,message:d.message}}
  const cd=r.headers.get('Content-Disposition')||'';const star=/filename\*=UTF-8''([^;]+)/i.exec(cd),plain=/filename="?([^";]+)"?/i.exec(cd);
  const name=star?decodeURIComponent(star[1]):plain?plain[1]:'download';
  const a=$('a',{href:URL.createObjectURL(await r.blob()),download:name});document.body.append(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(a.href),1000);
  return{ok:true,message:'Downloaded '+name};
}
function converter(m){
  const msg=$('span',{className:'msg'}),pick=$('select',{'aria-label':'Conversion'}),go=$('button',{type:'submit'},'Download');
  for(const c of m.conversions)pick.append($('option',{value:c.key},c.label));
  let input;const swap=()=>{const c=m.conversions.find(x=>x.key===pick.value);
    const next=c.input==='file'?$('input',{type:'file',name:'file',accept:c.accept||'','aria-label':c.label}):$('input',{type:'text',name:'text',placeholder:c.placeholder||'','aria-label':c.label,autocomplete:'off'});
    input?input.replaceWith(next):0;input=next};
  swap();pick.onchange=swap;
  const form=$('form',{className:'conv'},$('label',{},'Convert & download'),$('div',{className:'row'},pick,input,go),msg);
  form.onsubmit=async e=>{e.preventDefault();go.disabled=true;msg.className='msg';msg.textContent='Converting…';
    try{const r=await download('/api/modules/'+m.name+'/convert/'+pick.value,new FormData(form));msg.className='msg '+(r.ok?'ok':'bad');msg.textContent=r.message}
    finally{go.disabled=false}};
  return form;
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
  if(m.active&&m.conversions.length)sec.append(converter(m));
  sec.show=(ok,text)=>{msg.className='msg '+(ok?'ok':'bad');msg.textContent=text};
  const runTest=async(target,prefix)=>{target.show(true,prefix+'Testing…');const t=await call('POST','/api/modules/'+m.name+'/test');target.show(t.data.ok,prefix+t.data.message)};
  form.onsubmit=async e=>{e.preventDefault();
    const r=await call('PUT','/api/modules/'+m.name,Object.fromEntries(new FormData(form)));
    if(!r.ok)return sec.show(false,r.data.message);
    const fresh=render(r.data);sec.replaceWith(fresh);tabs[m.name].mark(r.data);await runTest(fresh,'Saved. ')};
  test.onclick=()=>runTest(sec,'');
  return sec;
}
async function copy(text,msg){try{await navigator.clipboard.writeText(text);msg.className='msg ok';msg.textContent='Copied'}catch{msg.className='msg bad';msg.textContent='Copy failed: select the text instead'}}
function clientsPanel(info){
  const pick=$('select',{'aria-label':'Client'}),mode=$('select',{'aria-label':'Connection'});
  mode.append($('option',{value:'http'},'HTTP: use this running server'),$('option',{value:'stdio'},'stdio: the client starts the server'));
  const active=info.modules.filter(m=>m.active),scope=$('div',{className:'scope'});
  for(const m of active)scope.append($('label',{},$('input',{type:'checkbox',value:m.name}),m.title));
  if(active.length)scope.append($('label',{title:'Only tools that change nothing'},$('input',{type:'checkbox',value:'readonly'}),'Read-only'));
  const out=$('div'),sec=$('section',{id:'panel-clients',role:'tabpanel','aria-labelledby':'tab-clients'},
    $('p',{className:'status'},'Download a config for your MCP client, made for this server ('+info.mcpUrl+').'),
    $('div',{className:'pick'},pick,mode));
  if(active.length)sec.append($('label',{},'Only these tools (none = all configured; fewer tools, fewer tokens)'),scope);
  sec.append(out);
  let data;
  const show=()=>{const c=data.clients.find(x=>x.key===pick.value)||data.clients[0],msg=$('span',{className:'msg'});
    const parts=[];
    if(c.transport!==mode.value)parts.push($('p',{className:'help'},c.title+' supports '+c.transport+' only, so this config uses '+c.transport+'.'));
    if(c.locations.length){
      const list=$('ul',{className:'where'});
      for(const l of c.locations){const cp=$('button',{type:'button',className:'ghost',style:'padding:0 6px;font-size:12px'},'Copy path');cp.onclick=()=>copy(l.path,msg);
        list.append($('li',{},l.label+': ',$('code',{},l.path),' ',cp,l.exists?$('span',{className:'badge'},' exists: add the entry under '+c.mergeInto+' instead of replacing the file'):''))}
      parts.push($('label',{},'Save as ',$('code',{},c.fileName),' in one of'),list);
    }
    const dl=$('button',{type:'button'},'Download '+c.fileName),cpy=$('button',{type:'button',className:'ghost'},'Copy');
    dl.onclick=()=>{const a=$('a',{href:URL.createObjectURL(new Blob([c.content],{type:'application/octet-stream'})),download:c.fileName});
      document.body.append(a);a.click();a.remove();setTimeout(()=>URL.revokeObjectURL(a.href),1000);msg.className='msg ok';
      msg.textContent=c.locations.length?'Saved to your downloads; move it to the path above'+(c.fileName.startsWith('.')?' and name it '+c.fileName:'')+'.':'Saved to your downloads.'};
    cpy.onclick=()=>copy(c.content,msg);
    parts.push($('pre',{},c.content),$('div',{className:'row',style:'margin-top:8px'},dl,cpy,msg),$('p',{className:'help'},c.steps));
    if(c.command){const cc=$('button',{type:'button',className:'ghost'},'Copy');cc.onclick=()=>copy(c.command,msg);
      parts.push($('label',{},'Or run'),$('div',{className:'pick'},$('pre',{style:'flex:1;margin:0'},c.command),cc))}
    out.replaceChildren(...parts)};
  const load=async()=>{const tools=[...scope.querySelectorAll('input:checked')].map(x=>x.value);
    const r=await call('GET','/api/clients/?transport='+mode.value+'&tools='+encodeURIComponent(tools.join(',')));
    if(!r.ok){out.replaceChildren($('p',{className:'msg bad'},r.data.message||'Failed'));return}
    data=r.data;if(!pick.options.length){for(const c of data.clients)pick.append($('option',{value:c.key},c.title));
      let last;try{last=sessionStorage.getItem('mcpClient')}catch{}if(last&&data.clients.some(c=>c.key===last))pick.value=last}
    show()};
  pick.onchange=()=>{try{sessionStorage.setItem('mcpClient',pick.value)}catch{}show()};mode.onchange=load;scope.onchange=load;
  load();
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
  const t=$('button',{type:'button',className:'tab',id:'tab-'+m.name,role:'tab','aria-controls':'panel-'+m.name},...(m.title?[dot,m.title]:[m.label]));
  t.mark=v=>{dot.className='dot'+(v.active?' on':'');t.title=v.active?'Configured':'Not configured'};if(m.title)t.mark(m);
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
  bar.append(tab({name:'clients',label:'MCP clients'}));box.append(clientsPanel(r.data));
  select(tabs[first]?first:r.data.modules.length?r.data.modules[0].name:'clients');
  document.getElementById('file').textContent='Settings are stored in '+r.data.settingsFile;
})();
</script>
</body>
</html>
""";
}
