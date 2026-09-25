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

  const stats = fs.statSync(filePath);
  const fileSizeMB = (stats.size / (1024 * 1024)).toFixed(2);
  console.log(`File: ${filePath}`);
  console.log(`Size: ${fileSizeMB} MB (${stats.size} bytes)`);

  // VirusTotal standard endpoint has 32MB limit. For larger files, use the upload_url flow.
  const LARGE_FILE_THRESHOLD = 32 * 1024 * 1024; // 32MB
  const isLargeFile = stats.size > LARGE_FILE_THRESHOLD;

  let uploadUrl = 'https://www.virustotal.com/api/v3/files';
  if (isLargeFile) {
    console.log('File exceeds 32MB, using large file upload flow...');
    console.log('Requesting upload URL from VirusTotal...');
    
    const urlResponse = await fetch('https://www.virustotal.com/api/v3/files/upload_url', {
      method: 'GET',
      headers: { 'x-apikey': apiKey },
    });
    
    const urlText = await urlResponse.text();
    if (!urlResponse.ok) {
      console.error(`Failed to get upload URL (${urlResponse.status} ${urlResponse.statusText})`);
      console.error(urlText);
      process.exit(1);
    }
    
    let urlPayload;
    try {
      urlPayload = JSON.parse(urlText);
    } catch (error) {
      console.error('Invalid JSON response for upload URL');
      console.error(urlText);
      process.exit(1);
    }
    
    uploadUrl = urlPayload.data;
    console.log(`Got upload URL: ${uploadUrl}`);
  }

  console.log('Uploading file to VirusTotal...');
  const fileData = fs.readFileSync(filePath);
  const form = new FormData();
  form.append('file', new Blob([fileData], { type: 'application/octet-stream' }), path.basename(filePath));

  const response = await fetch(uploadUrl, {
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
  // Web UI URL for viewing analysis results (not the API endpoint)
  const webAnalysisUrl = analysisId ? `https://www.virustotal.com/gui/file-analysis/${analysisId}` : 'https://www.virustotal.com/gui/';

  console.log(`Upload complete!`);
  console.log(`Analysis ID: ${analysisId || 'n/a'}`);
  console.log(`View results: ${webAnalysisUrl}`);
}

main().catch((error) => {
  console.error('Unexpected error while uploading to VirusTotal.');
  console.error(error);
  process.exit(1);
});
