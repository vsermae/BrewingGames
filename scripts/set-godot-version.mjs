// 把 tag 名写进 Godot 导出预设，打出来的包名与 Release 对得上。
// 用法：node scripts/set-godot-version.mjs v1.4.0   或   CNB_BRANCH=v1.4.0 node scripts/set-godot-version.mjs
// 不带版本时保持原样：手动触发（web_trigger）不该改动任何文件。
//
// 改了工作区里的文件却不提交，产物与仓库就会悄悄对不上；所以先看「这次构建确实
// 把 tag 写进预设」这件事是否还有意义——见下面 modify_resources 的判断。
import { readFileSync, writeFileSync } from 'node:fs';

const raw = process.argv[2] || process.env.CNB_BRANCH || '';
const version = raw.replace(/^v/, '').trim();

if (!/^\d+\.\d+\.\d+/.test(version)) {
  console.log(`版本号 "${raw}" 不是 x.y.z 形式，导出预设保持原样`);
  process.exit(0);
}

// 只取前三段：Godot 的 file_version 要 4 段纯数字，产物名却不想带尾巴
const numeric = version.split('-')[0];
const fields = {
  'application/file_version': numeric,
  'application/product_version': numeric,
};

const path = new URL('../godot/export_presets.cfg', import.meta.url);
let cfg = readFileSync(path, 'utf8');

// Windows 预设里 application/modify_resources=false 时，Godot 根本不写 PE 版本资源，
// 这份元数据进不了产物；真要让版本号上产物，得在预设里打开它并配好 rcedit。
if (/^application\/modify_resources=false$/m.test(cfg)) {
  console.log('导出预设 modify_resources=false，版本元数据不会写进产物，跳过');
  process.exit(0);
}

for (const [key, value] of Object.entries(fields)) {
  // Godot 的 cfg 是「key=value」，字符串值必须带引号；写裸值会让引擎读不出这个选项。
  const line = new RegExp(`^${key.replace(/[./]/g, '\\.')}=.*$`, 'gm');
  if (line.test(cfg)) cfg = cfg.replace(line, `${key}="${value}"`);
}
writeFileSync(path, cfg);
console.log(`导出预设版本 → ${version}`);
