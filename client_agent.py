import json
import os
from pathlib import Path
from typing import List, Dict, Any

# Load configuration
CONFIG_PATH = "config.json"
try:
    with open(CONFIG_PATH, 'r') as f:
        config = json.load(f)
except FileNotFoundError:
    print(f"Error: {CONFIG_PATH} not found. Cannot initialize agent.")
    config = None

def load_game_reports(config: Dict[str, Any]) -> List[Dict[str, Any]]:
    """
    Scans configured roots for games and generates the game_reports list
    for the Local Manifest.
    """
    if not config or 'game_scan_roots' not in config or 'folder_to_game_map' not in config:
        print("Configuration missing required keys for scanning.")
        return []

    reports = []
    game_map = config['folder_to_game_map']
    
    for root_str in config['game_scan_roots']:
        root = Path(root_str)
        if not root.is_dir():
            print(f"Warning: Scan root not found or is not a directory: {root_str}")
            continue
        
        print(f"--- Scanning {root_str} ---")

        # Walk through the directories in the root
        for entry in root.rglob('*'):
            if entry.is_dir() and entry.name in game_map:
                game_id = game_map[entry.name]
                
                # Simulate version detection (in a real scenario, this would involve reading metadata)
                # We assume a simple version suffix for demonstration.
                version = "1.0.0" 
                if "v" in entry.name:
                    version = entry.name.split('_')[-1]

                report = {
                    "game_id": game_id,
                    "installation_path": str(entry.resolve()),
                    "detected_version": version,
                    "is_current": True # Assume current until proven otherwise
                }
                reports.append(report)
                print(f"Discovered Game: {entry.name} -> ID: {game_id}")

    return reports

def generate_local_manifest(scan_roots: List[str]) -> Dict[str, Any]:
    """
    Simulates the full Local Manifest generation process, including placeholder 
    for the opaque machine_state.
    """
    if not config:
        return {"error": "Configuration not loaded"}

    # 1. Game Discovery
    game_reports = load_game_reports(config)
    
    # 2. Placeholder for Opaque State (from MachineState.ps1)
    # In a production system, this would be the actual JSON output from the engine.
    opaque_machine_state = {
        "cpu_count": 8,
        "total_ram_gb": 32,
        "system_theme": "dark_mode",
        "disk_utilization_percent": 45
    }
    
    # 3. Construct manifest
    manifest = {
        "client_id": config.get("agent_id", "TEMP_DEFAULT_ID"),
        "device_pin": "A1B2C3D4E5F6", # Placeholder for hardware PIN
        "timestamp_utc": os.popen('date -u +"%Y-%m-%dT%H:%M:%SZ"').read().strip(),
        "game_reports": game_reports,
        "machine_state": opaque_machine_state
    }
    
    return manifest

if __name__ == "__main__":
    manifest = generate_local_manifest(config.get('game_scan_roots', []))
    print("\n===== GENERATED LOCAL MANIFEST =====")
    print(json.dumps(manifest, indent=2))