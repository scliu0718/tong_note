namespace Tonote.App;

/// <summary>
/// WebView2 里运行的受控 HTML 编辑器。CSS 与 Mac 端 RichTextBridge / 编辑器设计稿 1:1：
/// 正文 #47474A / 14px / 行高 1.35、标题 40/32/24/18、引用绿竖线、代码块圆角、
/// 列表标记蓝、表格 100pt 列宽与 #D8D9DE 边框、图片最大 480px。
/// 浏览器 contenteditable 会产出 div / font / tbody / 绝对路径图片，
/// 这里在 JS 侧先规整成三端约定的受控标签集，C# 侧再用 RichTextHtmlCodec 校验一遍。
/// </summary>
public static class EditorHtml
{
    public const string Content = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<title>同笔记编辑器</title>
<style>
  :root { color-scheme: light; }
  * { box-sizing: border-box; }
  html, body { margin: 0; padding: 0; height: 100%; background: #FFFFFF; }
  body {
    font-family: "Microsoft YaHei UI", "PingFang SC", "Segoe UI", Inter, sans-serif;
    color: #47474A;
    -webkit-font-smoothing: antialiased;
    text-rendering: optimizeLegibility;
    scroll-behavior: smooth;
  }
  #editor {
    outline: none; min-height: 100%;
    padding: 28px 28px 96px;
    font-size: 14px; line-height: 1.35;
    letter-spacing: .005em;
    max-width: 680px;
    caret-color: #1A9E5C;
    text-wrap: pretty;
  }
  ::selection { background: #CFE9D9; color: #1D1D1F; }
  #editor[contenteditable="false"] { background: #F8FCFA; }
  #editor:empty::before {
    content: "开始写点什么…"; color: #A1A1A6;
    animation: tn-fade .25s ease;
  }
  @keyframes tn-fade { from { opacity: 0; } to { opacity: 1; } }
  /* 细滚动条：与外层 Avalonia 9px 滚动条同一视觉 */
  ::-webkit-scrollbar { width: 9px; height: 9px; }
  ::-webkit-scrollbar-track { background: transparent; }
  ::-webkit-scrollbar-thumb {
    background: #C9CCD4; border-radius: 9px; border: 2px solid #FFFFFF;
  }
  ::-webkit-scrollbar-thumb:hover { background: #A1A1A6; }
  p, div { margin: 0 0 3px; min-height: 1.2em; }
  h1 { font-size: 40px; margin: 18px 0 8px; line-height: 1.2; }
  h2 { font-size: 32px; margin: 15px 0 6px; line-height: 1.25; }
  h3 { font-size: 24px; margin: 12px 0 5px; line-height: 1.3; }
  h4 { font-size: 18px; margin: 10px 0 4px; line-height: 1.3; }
  h5 { font-size: 16px; margin: 10px 0 4px; line-height: 1.3; }
  h6 { font-size: 14px; margin: 10px 0 4px; line-height: 1.3; }
  h1, h2, h3, h4, h5, h6 {
    font-weight: 700; color: #1D1D1F;
    letter-spacing: -.02em;
  }
  blockquote {
    margin: 6px 0; padding: 6px 12px;
    border-left: 3px solid #1A9E5C; border-radius: 0 6px 6px 0;
    background: #F7FAF8; color: #86868B; font-size: 13.5px;
  }
  pre {
    margin: 4px 0; padding: 10px 12px;
    background: #F5F5F7; border: 1px solid #ECECEF; border-radius: 6px;
    font-family: Consolas, "Courier New", monospace;
    white-space: pre-wrap; font-size: 12.5px; line-height: 1.7;
  }
  /* 列表层级与 Mac / Android 对齐：扁平 <li data-level="n"> 存层级，不嵌套 <ul>。
     Tab/Shift+Tab 改 data-level（0-3，见 keydown），这里按层级缩进 + 切编号体系（1.→a.→i.）。
     标记用 CSS 自绘、不进正文 HTML，data-level 属性由清洗白名单保留。 */
  ul, ol { list-style: none; margin: 6px 0; padding-left: 8px; }
  /* min-height：刚回车出的空列表项也占一行，否则绝对定位的标记会叠到下一块 */
  li { position: relative; margin: 0; padding-left: 22px; line-height: 1.3; min-height: 1.3em; }
  li::before { position: absolute; left: 4px; top: 0; color: #2E7CF6; line-height: inherit; }
  li[data-level="1"] { margin-left: 28px; }
  li[data-level="2"] { margin-left: 56px; }
  li[data-level="3"] { margin-left: 84px; }
  li[data-level="4"] { margin-left: 112px; }
  li[data-level="5"] { margin-left: 140px; }
  li[data-level="6"] { margin-left: 168px; }
  li[data-level="7"] { margin-left: 196px; }
  /* 无序：一级实心、子级空心圆点，同字形同尺寸（left/top 按 Windows 14px 字号微调） */
  ul > li::before {
    content: ""; box-sizing: border-box; width: 6px; height: 6px;
    border-radius: 50%; background: #2E7CF6; border: 1.2px solid #2E7CF6; left: 5px; top: 5.5px;
  }
  ul > li[data-level]::before { background: transparent; }
  /* 有序：1. → a. → i.，CSS 计数器逐级编号，标记不进正文 */
  ol > li { counter-increment: n0; counter-reset: n1 n2; }
  ol > li::before { content: counter(n0) "."; left: 4px; }
  ol > li[data-level="1"] { counter-increment: n1; counter-reset: n2; }
  ol > li[data-level="1"]::before { content: counter(n1, lower-alpha) "."; }
  ol > li[data-level="2"], ol > li[data-level="3"], ol > li[data-level="4"],
  ol > li[data-level="5"], ol > li[data-level="6"], ol > li[data-level="7"] { counter-increment: n2; counter-reset: none; }
  ol > li[data-level="2"]::before, ol > li[data-level="3"]::before, ol > li[data-level="4"]::before,
  ol > li[data-level="5"]::before, ol > li[data-level="6"]::before, ol > li[data-level="7"]::before { content: counter(n2, lower-roman) "."; }
  /* W-08：三端契约是「满宽为默认、不写属性；data-fullwidth="0" 才是固定宽」 */
  table {
    border-collapse: collapse; table-layout: fixed;
    width: 100%;
    margin: 20px 0 10px; margin-left: 16px;
  }
  table[data-fullwidth="0"] { width: auto; }
  table[data-fullwidth="0"] td, table[data-fullwidth="0"] th { width: 100px; min-width: 100px; }
  td, th {
    border: 1px solid #D8D9DE; padding: 6px 10px;
    min-width: 40px; vertical-align: top;
    line-height: 1.3;
    transition: background-color .12s ease;
  }
  td:hover, th:hover { background: #FAFCFB; }
  td[data-align="center"], th[data-align="center"] { text-align: center; }
  td[data-align="right"], th[data-align="right"] { text-align: right; }
  td[data-align="justify"], th[data-align="justify"] { text-align: justify; }
  img {
    max-width: 480px; height: auto; border-radius: 6px;
    box-shadow: 0 1px 3px rgba(20,20,22,.10);
  }
  a {
    color: #1A73E6; text-decoration: underline;
    text-underline-offset: 2px; text-decoration-thickness: 1px;
  }
  hr { border: none; border-top: 1px solid #EFEFF2; margin: 18px 0; }
  #tnLoading {
    position: fixed; top: 8px; left: 50%; transform: translateX(-50%);
    display: none; align-items: center; gap: 6px;
    padding: 5px 12px; border-radius: 999px;
    background: rgba(255,255,255,0.94); box-shadow: 0 1px 6px rgba(0,0,0,0.15);
    font-size: 12px; color: #8A8F98; z-index: 10; pointer-events: none;
  }
  #tnLoading.on { display: flex; }
  #tnLoading i {
    width: 6px; height: 6px; border-radius: 50%; background: #069F21;
    animation: tnDot 1.2s infinite ease-in-out both;
  }
  #tnLoading i:nth-child(2) { animation-delay: 0.2s; }
  #tnLoading i:nth-child(3) { animation-delay: 0.4s; }
  @keyframes tnDot { 0%, 80%, 100% { opacity: 0.25; transform: scale(0.8); } 40% { opacity: 1; transform: scale(1.2); } }
</style>
</head>
<body>
<div id="tnLoading"><i></i><i></i><i></i></div>
<div id="editor" contenteditable="true" spellcheck="false"></div>
<script>
(function () {
  var editor = document.getElementById('editor');
  var timer = null;
  // W-09：加载基线。只有内容真的变了才 post changed，
  // 否则打开旧 Markdown 笔记仅失焦 / 切换也会被 C# 升级为 richText 并改写原稿。
  var baseline = '';
  var loadToken = 0;
  var loadingBar = document.getElementById('tnLoading');
  var loadingContent = false;

  var MAP = { div: 'p', b: 'strong', i: 'em', strike: 's', del: 's', article: 'p', section: 'p' };
  var UNWRAP = { tbody: 1, thead: 1, tfoot: 1, colgroup: 1 };
  // W-42：Core 解码不认 hr，JS 白名单也不保留，避免保存后静默丢块
  var BLOCK = { p: 1, h1: 1, h2: 1, h3: 1, h4: 1, h5: 1, h6: 1, blockquote: 1, pre: 1, ul: 1, ol: 1, table: 1 };
  var ATTRS = {
    img: ['src', 'width', 'height'], a: ['href'], span: ['style'],
    table: ['data-widths', 'data-fullwidth'],
    // W-10：td/th 的 style 必须保留，否则 Mac 老笔记的 style="background-color:#…" 一编辑就丢底色。
    // Core 的 RichTextTableCodec.BackgroundHex 已兼容 style，清洗时把 background-color 归一化为 data-bg。
    td: ['data-align', 'data-bg', 'style'], th: ['data-align', 'data-bg', 'style'], li: ['data-level']
  };
  var SIZES = { '1': 11, '2': 12, '3': 13, '4': 14, '5': 16, '6': 18, '7': 40 };

  function post(payload) {
    if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(payload);
  }
  // W-19：任何失败都必须可见，绝不静默 return
  function fail(title, reason, category) {
    post({ type: 'failure', title: title || '操作失败', reason: reason || '未知原因', category: category || 'editor' });
  }
  function diag(category, message) {
    post({ type: 'log', category: category || 'editor', message: message || '' });
  }
  function safeUrl(url) {
    try {
      var u = new URL(url, location.href);
      return u.protocol === 'http:' || u.protocol === 'https:' || u.protocol === 'mailto:';
    } catch (e) { return false; }
  }

  function cleanStyle(raw) {
    var keep = [];
    (raw || '').split(';').forEach(function (piece) {
      var at = piece.indexOf(':');
      if (at < 0) return;
      var key = piece.slice(0, at).trim().toLowerCase();
      var value = piece.slice(at + 1).trim();
      if (key === 'font-size' || key === 'color' || key === 'background-color') {
        if (value && value !== 'transparent') keep.push(key + ':' + value);
      }
    });
    return keep.join(';');
  }

  // W-10：把单元格 style 里的 background-color 归一化为 data-bg 契约属性，
  // 其余颜色 / 字号声明不写盘（默认色与引用灰由 Core 的 Encode 再兜一层）。
  function cellBackground(styleText) {
    if (!styleText) return null;
    var match = /(?:^|;)\s*background-color\s*:\s*([^;]+)/i.exec(styleText);
    if (!match) return null;
    var value = match[1].trim();
    if (!value || value === 'transparent') return null;
    var m = /^rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)(?:\s*,\s*([\d.]+))?\s*\)$/i.exec(value);
    if (m) {
      if (m[4] !== undefined && parseFloat(m[4]) <= 0.01) return null;
      return '#' + [m[1], m[2], m[3]].map(function (n) {
        return ('0' + parseInt(n, 10).toString(16)).slice(-2);
      }).join('').toUpperCase();
    }
    return value.toUpperCase();
  }

  function copyAttrs(source, target) {
    var tag = target.tagName.toLowerCase();
    var allowed = ATTRS[tag] || [];
    allowed.forEach(function (name) {
      var value = source.getAttribute(name);
      if (value === null) return;
      if (name === 'style') {
        if (tag === 'td' || tag === 'th') {
          var bg = cellBackground(value);
          if (bg) target.setAttribute('data-bg', bg);
          return; // 单元格 style 不原样写出，只留 data-bg / data-align
        }
        value = cleanStyle(value);
        if (!value) return;
      }
      if (name === 'src') {
        var at = value.indexOf('attachments/');
        if (at >= 0) value = value.substring(at).replace(/\\/g, '/');
        // W-10：非 attachments/ 的 src（blob:/data:/file:///绝对路径）原样保留，
        // 交给 C# SaveHtml 在写盘前转存为附件；无法转存则整篇拒写并报错。
      }
      target.setAttribute(name, value);
    });
  }

  function appendConverted(target, source) {
    for (var i = 0; i < source.childNodes.length; i++) {
      var child = convert(source.childNodes[i]);
      if (child) target.appendChild(child);
    }
  }

  function convert(node) {
    if (node.nodeType === 3) return document.createTextNode(node.nodeValue);
    if (node.nodeType !== 1) return null;
    var tag = node.tagName.toLowerCase();
    if (UNWRAP[tag]) {
      var frag = document.createDocumentFragment();
      appendConverted(frag, node);
      return frag;
    }
    if (tag === 'font' || tag === 'tt') {
      var span = document.createElement('span');
      var css = [];
      var color = node.getAttribute('color');
      if (color) css.push('color:' + color);
      var size = node.getAttribute('size');
      if (size) css.push('font-size:' + (SIZES[size] || 14) + 'px');
      var styled = cleanStyle(node.getAttribute('style'));
      if (styled) css.push(styled);
      if (css.length) span.setAttribute('style', css.join(';'));
      appendConverted(span, node);
      return span;
    }
    var mapped = MAP[tag] || tag;
    var out = document.createElement(mapped);
    copyAttrs(node, out);
    appendConverted(out, node);
    return out;
  }

  function clean() {
    var holder = document.createElement('div');
    for (var i = 0; i < editor.childNodes.length; i++) {
      // 块与块之间的换行（Encode 的块分隔符）是纯空白文本节点，不是内容：
      // 若包成 <p>\n</p> 写回，每保存一次空段落就翻倍，笔记会越存越大。
      var raw = editor.childNodes[i];
      if (raw.nodeType === 3 && !/[^\r\n]/.test(raw.nodeValue)) continue;
      var item = convert(raw);
      if (!item) continue;
      if (item.nodeType === 1 && BLOCK[item.tagName.toLowerCase()]) {
        holder.appendChild(item);
        continue;
      }
      var paragraph = holder.lastChild;
      if (!paragraph || paragraph.__tnParagraph !== true) {
        paragraph = document.createElement('p');
        paragraph.__tnParagraph = true;
        holder.appendChild(paragraph);
      }
      paragraph.appendChild(item);
    }
    return holder.innerHTML;
  }

  function changed() {
    if (loadingContent) return;
    if (timer) clearTimeout(timer);
    timer = setTimeout(function () {
      var html = clean();
      if (html === baseline) return; // 没有真实内容变化，不触发保存
      post({ type: 'changed', html: html });
    }, 350);
  }

  function applyCellStyles() {
    var cells = editor.querySelectorAll('td[data-bg], td[data-align], th[data-bg], th[data-align]');
    for (var i = 0; i < cells.length; i++) {
      var bg = cells[i].getAttribute('data-bg');
      var align = cells[i].getAttribute('data-align');
      if (bg) cells[i].style.backgroundColor = bg;
      if (align) cells[i].style.textAlign = align;
    }
  }

  editor.addEventListener('input', changed);
  // 点击编辑区获得焦点时通知宿主关闭工具条浮窗（浮窗 NOACTIVATE 后无失焦事件，靠这里补点外部关闭）
  editor.addEventListener('focus', function () { post({ type: 'editorFocus' }); });
  editor.addEventListener('blur', function () {
    if (loadingContent) return;
    if (timer) clearTimeout(timer);
    var html = clean();
    if (html === baseline) return; // W-09：仅失焦不算编辑，不写盘
    post({ type: 'changed', html: html });
  });
  editor.addEventListener('paste', function (event) {
    event.preventDefault();
    var data = event.clipboardData || window.clipboardData;
    // W-19：图片粘贴链路（W-15）未接通前，至少不能静默丢图——给出可见错误并记格式清单。
    var formats = [];
    if (data && data.types) {
      for (var i = 0; i < data.types.length; i++) formats.push(data.types[i]);
      var hasImage = false;
      if (data.items) {
        for (var j = 0; j < data.items.length; j++) {
          if ((data.items[j].type || '').indexOf('image/') === 0) hasImage = true;
        }
      }
      if (hasImage) {
        diag('paste', '图片粘贴被拦截，剪贴板格式：' + formats.join(', '));
        fail('操作失败', '暂不支持直接粘贴图片，请使用工具条的「插入图片」按钮选择文件。', 'paste');
        return;
      }
    }
    var text = data ? data.getData('text/plain') : '';
    document.execCommand('insertText', false, text);
    changed();
  });
  editor.addEventListener('click', function (event) {
    // W-18：链接不在编辑器内导航，改由系统浏览器打开（协议白名单由 C# 侧再验一次）
    var node = event.target;
    while (node && node !== editor && (!node.tagName || node.tagName.toLowerCase() !== 'a')) node = node.parentNode;
    if (!node || node === editor) return;
    var href = node.getAttribute('href');
    if (!href) return;
    event.preventDefault();
    post({ type: 'openExternal', url: href });
  });
  function currentItem() {
    var selection = window.getSelection();
    var node = selection && selection.anchorNode;
    while (node && node.nodeType !== 1) node = node.parentNode;
    return node && node.closest ? node.closest('li') : null;
  }
  var KNOWN_SIZES = [11, 12, 13, 14, 15, 16, 18, 20, 24, 28, 32, 40];
  function reportState() {
    try {
      var selection = window.getSelection();
      var node = selection && selection.anchorNode
        ? (selection.anchorNode.nodeType === 1 ? selection.anchorNode : selection.anchorNode.parentNode)
        : editor;
      var px = node ? parseInt(window.getComputedStyle(node).fontSize, 10) : 14;
      if (KNOWN_SIZES.indexOf(px) < 0) px = 14;
      post({ type: 'toolbarState', state: {
        bold: document.queryCommandState('bold'),
        italic: document.queryCommandState('italic'),
        underline: document.queryCommandState('underline'),
        strikeThrough: document.queryCommandState('strikeThrough'),
        fontSize: String(px),
        inTable: !!currentCell()
      }});
      post({ type: 'wordCount', count: (editor.innerText || '').replace(/\s/g, '').length });
    } catch (ignored) {}
  }
  editor.addEventListener('keyup', reportState);
  editor.addEventListener('mouseup', reportState);

  editor.addEventListener('keydown', function (event) {
    var key = (event.key || '').toLowerCase();
    var mod = event.ctrlKey || event.metaKey;
    if (mod && !event.altKey && (key === 'b' || key === 'i' || key === 'u' || key === 's')) {
      event.preventDefault();
      var cmd = key === 'b' ? 'bold' : key === 'i' ? 'italic' : key === 'u' ? 'underline' : 'strikeThrough';
      document.execCommand(cmd);
      changed();
      reportState();
      return;
    }
    if (mod && !event.altKey && key === 'k') {
      event.preventDefault(); post({ type: 'command', name: 'insertLink' }); return;
    }
    if (mod && !event.altKey && key === 's') {
      event.preventDefault(); post({ type: 'command', name: 'save' }); return;
    }
    if (mod && event.shiftKey && key === 'v') {
      event.preventDefault(); post({ type: 'command', name: 'pastePlain' }); return;
    }
    if (event.key === 'F5') {
      event.preventDefault(); post({ type: 'command', name: 'sync' }); return;
    }
    if (event.key === 'Escape') {
      post({ type: 'command', name: 'escape' }); return;
    }
    if (event.key === 'Tab') {
      var cell = currentCell();
      if (cell) {
        event.preventDefault();
        var cells = cell.closest('table').querySelectorAll('td, th');
        var index = Array.prototype.indexOf.call(cells, cell);
        var next = cells[index + (event.shiftKey ? -1 : 1)];
        if (next) { next.focus(); placeCaret(next); }
        else { window.tn.insertRow(); }
        return;
      }
      var item = currentItem();
      if (item) {
        event.preventDefault();
        var level = parseInt(item.getAttribute('data-level') || '0', 10) + (event.shiftKey ? -1 : 1);
        level = Math.max(0, Math.min(3, level)); // 与 Mac changeListLevel 一致：0-3
        // 三端契约：0 级不写 data-level（Mac codec / Android 均如此），否则会被子级 CSS 当空心点
        if (level === 0) item.removeAttribute('data-level');
        else item.setAttribute('data-level', String(level));
        changed();
      }
      return;
    }
    if (event.key === 'Backspace') {
      var li = currentItem();
      if (li && window.getSelection().isCollapsed && li.textContent.trim() === '') {
        event.preventDefault();
        document.execCommand('outdent');
        changed();
      }
    }
  });

  function placeCaret(node) {
    var range = document.createRange();
    range.selectNodeContents(node);
    range.collapse(true);
    var selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
  }

  try { document.execCommand('styleWithCSS', false, false); } catch (ignored) {}

  function currentCell() {
    var selection = window.getSelection();
    if (!selection || !selection.anchorNode) return null;
    var node = selection.anchorNode;
    while (node && node.nodeType !== 1) node = node.parentNode;
    while (node && node !== editor) {
      var tag = node.tagName ? node.tagName.toLowerCase() : '';
      if (tag === 'td' || tag === 'th') return node;
      node = node.parentNode;
    }
    return null;
  }

  function currentRow() {
    var cell = currentCell();
    return cell ? cell.parentNode : null;
  }

  // 切换段样式（标题/正文）时清掉当前块内所有 inline font-size，
  // 避免之前手动设的字号盖过标题 CSS 字号（h1 40 / h2 32 / h3 24 / h4 18 / 正文 14）。
  // 只清字号，保留 color / background-color 等其它行内样式（对齐 Mac 保留文字颜色）。
  function clearBlockInlineFontSize() {
    var sel = window.getSelection();
    if (!sel || !sel.anchorNode) return;
    var block = sel.anchorNode.nodeType === 1 ? sel.anchorNode : sel.anchorNode.parentNode;
    while (block && block !== editor) {
      if (block.nodeType === 1 && BLOCK[block.tagName.toLowerCase()]) break;
      block = block.parentNode;
    }
    if (!block || block === editor) return;
    var spans = block.querySelectorAll('span[style]');
    for (var i = 0; i < spans.length; i++) {
      var span = spans[i];
      var kept = [];
      (span.getAttribute('style') || '').split(';').forEach(function (piece) {
        var p = piece.trim();
        if (!p || p.toLowerCase().indexOf('font-size') === 0) return;
        kept.push(p);
      });
      if (kept.length) {
        span.setAttribute('style', kept.join(';'));
      } else {
        // 字号是唯一样式：展开 span 为纯文本，避免留下空壳
        while (span.firstChild) span.parentNode.insertBefore(span.firstChild, span);
        span.parentNode.removeChild(span);
      }
    }
  }

  window.tn = {
    setContent: function (html, readOnly) {
      var token = ++loadToken;
      loadingBar.classList.remove('on');
      if (timer) clearTimeout(timer);
      var FIRST = 30, BATCH = 60;
      var tpl = document.createElement('template');
      tpl.innerHTML = html || '<p><br></p>';
      var nodes = Array.prototype.slice.call(tpl.content.childNodes);
      function reveal() {
        requestAnimationFrame(function () {
          requestAnimationFrame(function () {
            if (token === loadToken) editor.style.visibility = 'visible';
          });
        });
      }
      function finish() {
        applyCellStyles();
        baseline = clean(); // W-09：此后仅有真实内容差异才 post changed
        loadingContent = false;
        loadingBar.classList.remove('on');
        editor.contentEditable = readOnly ? 'false' : 'true';
        reveal();
        post({ type: 'loaded', html: baseline });
      }
      // 短笔记：一次写完。
      if (nodes.length <= FIRST) {
        editor.innerHTML = html || '<p><br></p>';
        finish();
        return;
      }
      // 长笔记：先写开头一部分并立刻显示，其余分批追加；追加期间只读且不触发保存。
      loadingContent = true;
      editor.contentEditable = 'false';
      editor.scrollTop = 0;
      editor.innerHTML = '';
      var index = 0;
      function append(count) {
        var frag = document.createDocumentFragment();
        for (var n = 0; n < count && index < nodes.length; n++, index++) frag.appendChild(nodes[index]);
        editor.appendChild(frag);
      }
      append(FIRST);
      loadingBar.classList.add('on');
      reveal();
      post({ type: 'partial' });
      (function step() {
        if (token !== loadToken) return;
        if (index >= nodes.length) { finish(); return; }
        append(BATCH);
        setTimeout(step, 0);
      })();
    },
    getHTML: clean,
    focus: function () { editor.focus(); },
    exec: function (command, value) {
      editor.focus();
      document.execCommand(command, false, value === undefined ? null : value);
      changed();
    },
    block: function (tag) {
      editor.focus();
      document.execCommand('formatBlock', false, tag);
      // 对齐 Mac applyBlockStyle：切标题时清掉旧 inline 字号，让标题 CSS 字号生效；
      // 字号基准随块样式走，用户之后仍可手动用字号菜单改。
      clearBlockInlineFontSize();
      changed();
      reportState(); // 字号工具条自动同步为标题对应字号（40/32/24/18/14）
    },
    setFontSize: function (px) {
      editor.focus();
      document.execCommand('fontSize', false, '7');
      var fonts = editor.querySelectorAll('font[size="7"]');
      for (var i = 0; i < fonts.length; i++) {
        var span = document.createElement('span');
        span.style.fontSize = px + 'px';
        while (fonts[i].firstChild) span.appendChild(fonts[i].firstChild);
        fonts[i].parentNode.replaceChild(span, fonts[i]);
      }
      changed();
    },
    insertImage: function (src) {
      editor.focus();
      document.execCommand('insertHTML', false, '<img src="' + src + '">');
      changed();
    },
    insertLink: function (url, text) {
      // W-18：白名单 + DOM API（不拼 HTML 字符串），避免 javascript: 注入与引号截断
      if (!safeUrl(url)) {
        fail('无法插入链接', '仅支持 http、https、mailto 协议，请输入有效的链接地址。', 'editor');
        return;
      }
      editor.focus();
      var anchor = document.createElement('a');
      anchor.setAttribute('href', url);
      anchor.textContent = text || url;
      var selection = window.getSelection();
      var range = selection && selection.rangeCount ? selection.getRangeAt(0) : null;
      if (range && editor.contains(range.commonAncestorContainer)) {
        range.deleteContents();
        range.insertNode(anchor);
        range.setStartAfter(anchor);
        range.collapse(true);
        selection.removeAllRanges();
        selection.addRange(range);
      } else {
        editor.appendChild(anchor);
      }
      changed();
    },
    insertTable: function (rows, cols) {
      // W-08：新建表默认固定宽（data-fullwidth="0"），每列 100px，不铺满笔记
      var html = '<table data-fullwidth="0">';
      for (var r = 0; r < rows; r++) {
        html += '<tr>';
        for (var c = 0; c < cols; c++) html += '<td style="width:100px"><p><br></p></td>';
        html += '</tr>';
      }
      html += '</table><p><br></p>';
      editor.focus();
      document.execCommand('insertHTML', false, html);
      applyCellStyles();
      changed();
    },
    insertRow: function () {
      var row = currentRow();
      if (!row) { fail('操作失败', '请先把光标放到表格单元格内。', 'table'); return; }
      var table = row.closest('table');
      if (table.querySelectorAll('tr').length >= 32) {
        fail('操作失败', '表格最多 32 行。', 'table');
        diag('table', '撞到 32 行上限');
        return;
      }
      var source = Array.prototype.slice.call(row.querySelectorAll('td, th'));
      var fresh = document.createElement('tr');
      for (var i = 0; i < source.length; i++) {
        var cell = document.createElement('td');
        cell.innerHTML = '<p><br></p>';
        // W-32：沿用相邻行的对齐与底色
        var align = source[i].getAttribute('data-align');
        var bg = source[i].getAttribute('data-bg');
        if (align) cell.setAttribute('data-align', align);
        if (bg) cell.setAttribute('data-bg', bg);
        fresh.appendChild(cell);
      }
      table.insertBefore(fresh, row.nextSibling);
      applyCellStyles();
      changed();
    },
    insertColumn: function () {
      var cell = currentCell();
      if (!cell) { fail('操作失败', '请先把光标放到表格单元格内。', 'table'); return; }
      var table = cell.closest('table');
      if (!table) return;
      var rows = table.querySelectorAll('tr');
      if (rows.length && rows[0].children.length >= 100) {
        fail('操作失败', '表格最多 100 列。', 'table');
        diag('table', '撞到 100 列上限');
        return;
      }
      var index = Array.prototype.indexOf.call(cell.parentNode.children, cell);
      for (var i = 0; i < rows.length; i++) {
        var fresh = document.createElement('td');
        fresh.innerHTML = '<p><br></p>';
        var adjacent = rows[i].children[Math.max(0, index - 1)];
        if (adjacent && adjacent !== cell) {
          var align = adjacent.getAttribute('data-align');
          var bg = adjacent.getAttribute('data-bg');
          if (align) fresh.setAttribute('data-align', align);
          if (bg) fresh.setAttribute('data-bg', bg);
        }
        rows[i].insertBefore(fresh, rows[i].children[index + 1] || null);
      }
      // W-32：列数变化后旧权重失效，交回 Core 重新等分
      table.removeAttribute('data-widths');
      applyCellStyles();
      changed();
    },
    removeRow: function () {
      var row = currentRow();
      if (!row) { fail('操作失败', '请先把光标放到表格单元格内。', 'table'); return; }
      if (row.parentNode.querySelectorAll('tr').length <= 1) {
        fail('操作失败', '表格至少需要保留一行。', 'table');
        return;
      }
      row.parentNode.removeChild(row);
      diag('table', '删除一行');
      changed();
    },
    removeColumn: function () {
      var cell = currentCell();
      if (!cell) { fail('操作失败', '请先把光标放到表格单元格内。', 'table'); return; }
      var index = Array.prototype.indexOf.call(cell.parentNode.children, cell);
      var table = cell.closest('table');
      var rows = table.querySelectorAll('tr');
      if (rows[0].children.length <= 1) {
        fail('操作失败', '表格至少需要保留一列。', 'table');
        return;
      }
      for (var i = 0; i < rows.length; i++) {
        if (rows[i].children[index]) rows[i].removeChild(rows[i].children[index]);
      }
      table.removeAttribute('data-widths');
      applyCellStyles();
      diag('table', '删除一列');
      changed();
    },
    removeTable: function () {
      var cell = currentCell();
      if (!cell) { fail('操作失败', '请先把光标放到表格单元格内。', 'table'); return; }
      var table = cell.closest('table');
      if (table && table.parentNode) {
        table.parentNode.removeChild(table);
        diag('table', '删除整表');
        changed();
      }
    },
    setFullWidth: function (value) {
      var cell = currentCell();
      if (!cell) return;
      var table = cell.closest('table');
      if (!table) return;
      // W-08：满宽=移除属性（契约默认），固定宽=写 "0"；"1" 是非法值
      if (value === '1' || value === 'full' || value === true) table.removeAttribute('data-fullwidth');
      else table.setAttribute('data-fullwidth', '0');
      changed();
    },
    setCellAlign: function (value) {
      var cell = currentCell();
      if (!cell) { fail('操作失败', '请先把光标放到表格单元格内。', 'table'); return; }
      // W-42：契约规定「左对齐＝不写属性」
      if (value === 'left' || !value) cell.removeAttribute('data-align');
      else cell.setAttribute('data-align', value);
      cell.style.textAlign = value === 'left' ? '' : value;
      changed();
    },
    setCellBackground: function (value) {
      var cell = currentCell();
      if (!cell) { fail('操作失败', '请先把光标放到表格单元格内。', 'table'); return; }
      // W-42：清除底色＝移除属性，不写 data-bg="#FFFFFF"
      if (!value || value === '#FFFFFF') cell.removeAttribute('data-bg');
      else cell.setAttribute('data-bg', value);
      cell.style.backgroundColor = !value || value === '#FFFFFF' ? '' : value;
      changed();
    }
  };

  post({ type: 'ready' });
})();
</script>
</body>
</html>
""";
}
