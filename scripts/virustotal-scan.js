#!/usr/bin/env node

const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

const configPath = path.resolve(__dirname, '..', 'virustotal-config.json');
const filePath = path.resolve(__dirname, '..', 'bin/Release/net10.0/publish/MultiPing.exe');

function loadApiKey() {
  if (!fs.existsSync(configPath)) {
    console.error(`Configuration file not found: ${configPath}`);
    console.error(`Please copy virustotal-config.json.sample to virustotal-config.json and add your API key.`);
    process.exit(1);
  }

  const configContent = fs.readFileSync(configPath, 'utf8');
  let config;
  try {
    config = JSON.parse(configContent);
  } catch (err) {
    console.error(`Failed to parse configuration file: ${configPath}`);
    console.error(err.message);
    process.exit(1);
  }

  if (!config.apiKey || config.apiKey.trim() === '' || config.apiKey === 'your-api-key-here') {
    console.error('API key not configured in virustotal-config.json');
    console.error('Please edit virustotal-config.json and set your VirusTotal API key.');
    process.exit(1);
  }

  return config.apiKey.trim();
}

function ensureReleaseBuild() {
  if (fs.existsSync(filePath)) return;

  console.log('Release binary not found. Building it now...');
  const result = spawnSync(
    process.platform === 'win32' ? 'dotnet.exe' : 'dotnet',
    ['publish', 'MultiPing.csproj', '-c', 'Release', '-r', 'win-x64', '--no-restore', '-o', 'bin/Release/net10.0/publish'],
    {
      cwd: path.resolve(__dirname, '..'),
      stdio: 'inherit',
      shell: false,
    }
  );

  if (result.error) {
    throw result.error;
  }

  if (result.status !== 0) {
    process.exit(result.status || 1);
  }
}

async function main() {
  const apiKey = loadApiKey();

  ensureReleaseBuild();

  if (!fs.existsSync(filePath)) {
    console.error(`File not found: ${filePath}`);
    process.exit(1);
  }

  const fileData = fs.readFileSync(filePath);
  const form = new FormData();
  form.append('file', new Blob([fileData], { type: 'application/octet-stream' }), path.basename(filePath));

  const response = await fetch('https://www.virustotal.com/api/v3/files', {
    method: 'POST',
    headers: {
      'x-apikey': apiKey,
    },
    body: form,
  });

  const text = await response.text();

  if (!response.ok) {
    console.error(`VirusTotal upload failed (${response.status} ${response.statusText})`);
    console.error(text);
    process.exit(1);
  }

  let payload;
  try {
    payload = JSON.parse(text);
  } catch (error) {
    console.error('VirusTotal returned an invalid JSON response.');
    console.error(text);
    process.exit(1);
  }

  const analysisId = payload.data?.id;
  const analysisUrl = payload.data?.links?.self || 'https://www.virustotal.com/gui/';

  console.log(`Uploaded ${filePath}`);
  console.log(`Analysis ID: ${analysisId || 'n/a'}`);
  console.log(`View results: ${analysisUrl}`);
}

main().catch((error) => {
  console.error('Unexpected error while uploading to VirusTotal.');
  console.error(error);
  process.exit(1);
});
