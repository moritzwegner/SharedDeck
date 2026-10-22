// Generates layout.json for an MSFS community package (all files except layout.json/manifest.json).
// Usage: node make-layout.js <package directory>
const fs = require('fs');
const path = require('path');

const root = path.resolve(process.argv[2] || '.');
const content = [];

const walk = (dir) => {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      walk(full);
      continue;
    }
    const relative = path.relative(root, full).split(path.sep).join('/');
    if (relative === 'layout.json' || relative === 'manifest.json') {
      continue;
    }
    const stat = fs.statSync(full);
    // Windows FILETIME: 100 ns intervals since 1601-01-01
    const date = BigInt(Math.floor(stat.mtimeMs)) * 10000n + 116444736000000000n;
    content.push({ path: relative, size: stat.size, date: Number(date) });
  }
};

walk(root);
content.sort((a, b) => a.path.localeCompare(b.path));
fs.writeFileSync(path.join(root, 'layout.json'), JSON.stringify({ content }, null, 2));
console.log(`layout.json: ${content.length} files`);
