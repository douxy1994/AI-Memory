import fs from "node:fs";
import path from "node:path";
import process from "node:process";

const windowsRoot = path.resolve(
  path.dirname(new URL(import.meta.url).pathname),
  "..",
);
const appRoot = path.join(windowsRoot, "src", "AIMemory.Windows");
const failures = [];

function filesUnder(root, suffix) {
  const result = [];
  for (const entry of fs.readdirSync(root, { withFileTypes: true })) {
    if (entry.name === "bin" || entry.name === "obj") continue;
    const fullPath = path.join(root, entry.name);
    if (entry.isDirectory()) {
      result.push(...filesUnder(fullPath, suffix));
    } else if (fullPath.endsWith(suffix)) {
      result.push(fullPath);
    }
  }
  return result;
}

function resourceMap(language) {
  const file = path.join(
    appRoot,
    "Strings",
    language,
    "Resources.resw",
  );
  const xml = fs.readFileSync(file, "utf8");
  const values = new Map();
  for (const match of xml.matchAll(
    /<data name="([^"]+)"[^>]*><value>([\s\S]*?)<\/value><\/data>/g,
  )) {
    if (values.has(match[1])) {
      failures.push(`${language}: duplicate resource ${match[1]}`);
    }
    values.set(match[1], match[2]);
  }
  return values;
}

function placeholders(value) {
  return [...value.matchAll(/\{(\d+)(?::[^}]*)?\}/g)]
    .map((match) => match[1])
    .sort()
    .join(",");
}

const languages = ["zh-CN", "en-US"];
const resources = Object.fromEntries(
  languages.map((language) => [language, resourceMap(language)]),
);
const allKeys = new Set(
  languages.flatMap((language) => [...resources[language].keys()]),
);

for (const key of allKeys) {
  for (const language of languages) {
    if (!resources[language].has(key)) {
      failures.push(`${language}: missing resource ${key}`);
    }
  }
  if (languages.every((language) => resources[language].has(key))) {
    const expected = placeholders(resources[languages[0]].get(key));
    for (const language of languages.slice(1)) {
      const actual = placeholders(resources[language].get(key));
      if (actual !== expected) {
        failures.push(
          `${key}: placeholder mismatch ${languages[0]}=${expected} ${language}=${actual}`,
        );
      }
    }
  }
}

for (const file of filesUnder(appRoot, ".xaml")) {
  const source = fs.readFileSync(file, "utf8");
  for (const match of source.matchAll(/x:Uid="([^"]+)"/g)) {
    const uid = match[1];
    for (const language of languages) {
      if (![...resources[language].keys()].some(
        (key) => key.startsWith(`${uid}.`),
      )) {
        failures.push(
          `${path.relative(windowsRoot, file)}: x:Uid ${uid} has no ${language} property resource`,
        );
      }
    }
  }
  for (const match of source.matchAll(/<[^!?][\s\S]*?>/g)) {
    const tag = match[0];
    if (
      /(?:Text|Content|Header|PlaceholderText|Label|Title|Message)="[^"]*[\u3400-\u9fff][^"]*"/.test(tag)
      && !tag.includes("x:Uid=")
    ) {
      const line = source.slice(0, match.index).split("\n").length;
      failures.push(
        `${path.relative(windowsRoot, file)}:${line}: localized XAML literal has no x:Uid`,
      );
    }
  }
}

for (const file of filesUnder(appRoot, ".cs")) {
  const source = fs.readFileSync(file, "utf8");
  if (/[\u3400-\u9fff]/.test(source)) {
    failures.push(
      `${path.relative(windowsRoot, file)}: contains a Chinese code-behind literal`,
    );
  }
  for (const match of source.matchAll(
    /LocalizationService\.(?:Get|Format)\(\s*"([^"]+)"/g,
  )) {
    for (const language of languages) {
      if (!resources[language].has(match[1])) {
        failures.push(
          `${path.relative(windowsRoot, file)}: missing ${language} dynamic resource ${match[1]}`,
        );
      }
    }
  }
}

const manifest = fs.readFileSync(
  path.join(appRoot, "Package.appxmanifest"),
  "utf8",
);
if (!manifest.includes('Description="ms-resource:AppDescription"')) {
  failures.push("Package.appxmanifest: AppDescription is not localized");
}
for (const language of languages) {
  if (!manifest.includes(`<Resource Language="${language}" />`)) {
    failures.push(`Package.appxmanifest: missing ${language} declaration`);
  }
}

if (failures.length > 0) {
  for (const failure of failures) console.error(failure);
  process.exit(1);
}

console.log(
  `Windows localization verified: ${allKeys.size} resources in ${languages.join(" and ")}.`,
);
