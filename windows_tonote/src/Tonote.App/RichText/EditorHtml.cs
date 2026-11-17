namespace Tonote.App;

/// <summary>
/// WebView2 里运行的受控 HTML 编辑器。CSS 与 Mac 端 RichTextBridge / 编辑器设计稿 1:1：
/// 正文 #47474A / 14px / 行高 1.85、标题 40/32/24/18、引用绿竖线、代码块圆角、
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
    font-size: 14px; line-height: 1.85;
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
  p, div { margin: 0 0 8px; min-height: 1.3em; }
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
    margin: 8px 0; padding: 6px 12px;
    border-left: 3px solid #1A9E5C; border-radius: 0 6px 6px 0;
    background: #F7FAF8; color: #86868B; font-size: 13.5px;
  }
  pre {
    margin: 8px 0; padding: 10px 12px;
    background: #F5F5F7; border: 1px solid #ECECEF; border-radius: 6px;
    font-family: Consolas, "Courier New", monospace;
    white-space: pre-wrap; font-size: 12.5px; line-height: 1.7;
  }
  ul, ol { margin: 6px 0; padding-left: 26px; }
  li { line-height: 1.3; padding-left: 2px; }
  li::marker { color: #2E7CF6; }
  table {
    border-collapse: collapse; table-layout: fixed;
    margin: 20px 0 10px; margin-left: 16px;
  }
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
  table[data-fullwidth="1"] { width: 100%; }
  img {
    max-width: 480px; height: auto; border-radius: 6px;
    box-shadow: 0 1px 3px rgba(20,20,22,.10);
  }
  a {
    color: #1A73E6; text-decoration: underline;
    text-underline-offset: 2px; text-decoration-thickness: 1px;
  }
  hr { border: none; border-top: 1px solid #EFEFF2; margin: 18px 0; }
</style>
</head>
<body>
<div id="editor" contenteditable="true" spellcheck="false"></div>
<script>
(function () {
  var editor = document.getElementById('editor');
  var timer = null;

  var MAP = { div: 'p', b: 'strong', i: 'em', strike: 's', del: 's', article: 'p', section: 'p' };
  var UNWRAP = { tbody: 1, thead: 1, tfoot: 1, colgroup: 1 };
  var BLOCK = { p: 1, h1: 1, h2: 1, h3: 1, h4: 1, h5: 1, h6: 1, blockquote: 1, pre: 1, ul: 1, ol: 1, table: 1, hr: 1 };
  var ATTRS = {
    img: ['src', 'width', 'height'], a: ['href'], span: ['style'],
    table: ['data-widths', 'data-fullwidth'],
    td: ['data-align', 'data-bg'], th: ['data-align', 'data-bg'], li: ['data-level']
  };
  var SIZES = { '1': 11, '2': 12, '3': 13, '4': 14, '5': 16, '6': 18, '7': 40 };

  function post(payload) {
    if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(payload);
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

  function copyAttrs(source, target) {
    var allowed = ATTRS[target.tagName.toLowerCase()] || [];
    allowed.forEach(function (name) {
      var value = source.getAttribute(name);
      if (value === null) return;
      if (name === 'style') {
        value = cleanStyle(value);
        if (!value) return;
      }
      if (name === 'src') {
        var at = value.indexOf('attachments/');
        if (at >= 0) value = value.substring(at).replace(/\\/g, '/');
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
      var item = convert(editor.childNodes[i]);
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
    if (timer) clearTimeout(timer);
    timer = setTimeout(function () { post({ type: 'changed', html: clean() }); }, 350);
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
  editor.addEventListener('blur', function () {
    if (timer) clearTimeout(timer);
    post({ type: 'changed', html: clean() });
  });
  editor.addEventListener('paste', function (event) {
    event.preventDefault();
    var text = (event.clipboardData || window.clipboardData).getData('text/plain');
    document.execCommand('insertText', false, text);
    changed();
  });
  editor.addEventListener('keydown', function (event) {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'b') {
      event.preventDefault(); document.execCommand('bold'); changed();
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

  window.tn = {
    setContent: function (html) {
      editor.innerHTML = html || '<p><br></p>';
      applyCellStyles();
      post({ type: 'loaded', html: clean() });
    },
    getHTML: clean,
    focus: function () { editor.focus(); },
    exec: function (command, value) {
      editor.focus();
      document.execCommand(command, false, value === undefined ? null : value);
      changed();
    },
    block: function (tag) { window.tn.exec('formatBlock', tag); },
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
      editor.focus();
      var label = text || url;
      document.execCommand('insertHTML', false, '<a href="' + url + '">' + label + '</a>');
      changed();
    },
    insertTable: function (rows, cols) {
      var html = '<table data-fullwidth="1">';
      for (var r = 0; r < rows; r++) {
        html += '<tr>';
        for (var c = 0; c < cols; c++) html += '<td><p><br></p></td>';
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
      if (!row) return;
      var cells = row.querySelectorAll('td, th').length || 1;
      var fresh = document.createElement('tr');
      for (var i = 0; i < cells; i++) {
        var cell = document.createElement('td');
        cell.innerHTML = '<p><br></p>';
        fresh.appendChild(cell);
      }
      row.parentNode.insertBefore(fresh, row.nextSibling);
      changed();
    },
    insertColumn: function () {
      var cell = currentCell();
      if (!cell) return;
      var table = cell.closest('table');
      if (!table) return;
      var rows = table.querySelectorAll('tr');
      for (var i = 0; i < rows.length; i++) {
        var fresh = document.createElement('td');
        fresh.innerHTML = '<p><br></p>';
        rows[i].appendChild(fresh);
      }
      changed();
    },
    removeRow: function () {
      var row = currentRow();
      if (row && row.parentNode.querySelectorAll('tr').length > 1) {
        row.parentNode.removeChild(row);
        changed();
      }
    },
    removeColumn: function () {
      var cell = currentCell();
      if (!cell) return;
      var index = Array.prototype.indexOf.call(cell.parentNode.children, cell);
      var table = cell.closest('table');
      var rows = table.querySelectorAll('tr');
      if (rows[0].children.length <= 1) return;
      for (var i = 0; i < rows.length; i++) {
        if (rows[i].children[index]) rows[i].removeChild(rows[i].children[index]);
      }
      changed();
    },
    removeTable: function () {
      var cell = currentCell();
      if (!cell) return;
      var table = cell.closest('table');
      if (table && table.parentNode) { table.parentNode.removeChild(table); changed(); }
    },
    setFullWidth: function (value) {
      var cell = currentCell();
      if (!cell) return;
      var table = cell.closest('table');
      if (!table) return;
      table.setAttribute('data-fullwidth', value);
      changed();
    },
    setCellAlign: function (value) {
      var cell = currentCell();
      if (!cell) return;
      cell.setAttribute('data-align', value);
      cell.style.textAlign = value;
      changed();
    },
    setCellBackground: function (value) {
      var cell = currentCell();
      if (!cell) return;
      cell.setAttribute('data-bg', value);
      cell.style.backgroundColor = value;
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
