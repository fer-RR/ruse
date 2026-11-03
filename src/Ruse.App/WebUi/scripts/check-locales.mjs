import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';

const webRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const sourceRoot = path.join(webRoot, 'src');
const localeRoot = path.join(sourceRoot, 'locales');

function walk(directory) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    const full = path.join(directory, entry.name);
    return entry.isDirectory() ? walk(full) : [full];
  });
}

function textOf(node) {
  return ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)
    ? node.text
    : null;
}

function sourceKeys() {
  const keys = new Set();
  for (const file of walk(sourceRoot)) {
    if (!/\.tsx?$/.test(file) || file.startsWith(localeRoot + path.sep)) continue;
    const source = ts.createSourceFile(
      file, fs.readFileSync(file, 'utf8'), ts.ScriptTarget.Latest, true,
      file.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS);
    const visit = node => {
      if (ts.isCallExpression(node) && ts.isIdentifier(node.expression) &&
          node.expression.text === 't' && node.arguments.length > 0) {
        const key = textOf(node.arguments[0]);
        if (key !== null) keys.add(key);
      }
      ts.forEachChild(node, visit);
    };
    visit(source);
  }
  return keys;
}

function localeEntries(file) {
  const source = ts.createSourceFile(
    file, fs.readFileSync(file, 'utf8'), ts.ScriptTarget.Latest, true, ts.ScriptKind.TS);
  const entries = new Map();
  const duplicates = [];
  const visit = node => {
    if (ts.isVariableDeclaration(node) && node.initializer &&
        ts.isObjectLiteralExpression(node.initializer)) {
      for (const property of node.initializer.properties) {
        if (!ts.isPropertyAssignment(property)) continue;
        const key = ts.isStringLiteral(property.name) || ts.isIdentifier(property.name)
          ? property.name.text
          : null;
        const value = textOf(property.initializer);
        if (key === null || value === null) continue;
        if (entries.has(key)) duplicates.push(key);
        else entries.set(key, value);
      }
    }
    ts.forEachChild(node, visit);
  };
  visit(source);
  return { entries, duplicates };
}

function placeholders(text) {
  return new Set([...text.matchAll(/\{([A-Za-z0-9_]+)\}/g)].map(match => match[1]));
}

const keys = sourceKeys();
const locales = fs.readdirSync(localeRoot)
  .filter(name => name.endsWith('.ts'))
  .map(name => ({ name: name.replace(/\.ts$/, ''), ...localeEntries(path.join(localeRoot, name)) }));

let failed = false;
for (const locale of locales) {
  const missing = [...keys].filter(key => !locale.entries.has(key));
  const placeholderErrors = [];
  for (const key of keys) {
    const value = locale.entries.get(key);
    if (value === undefined) continue;
    const expected = placeholders(key);
    const actual = placeholders(value);
    if (expected.size !== actual.size || [...expected].some(name => !actual.has(name))) {
      placeholderErrors.push(key);
    }
  }

  if (missing.length || locale.duplicates.length || placeholderErrors.length) {
    failed = true;
    console.log(`\n${locale.name}: ${missing.length} missing, ` +
      `${locale.duplicates.length} duplicate, ${placeholderErrors.length} placeholder mismatch`);
    for (const key of missing) console.log(`  missing: ${JSON.stringify(key)}`);
    for (const key of locale.duplicates) console.log(`  duplicate: ${JSON.stringify(key)}`);
    for (const key of placeholderErrors) console.log(`  placeholders: ${JSON.stringify(key)}`);
  } else {
    console.log(`${locale.name}: OK (${locale.entries.size} entries)`);
  }
}

if (failed) process.exitCode = 1;
