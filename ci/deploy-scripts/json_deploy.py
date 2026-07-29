import base64
import hashlib
import json
import os
import re
import sys
import urllib.parse
from datetime import datetime, timezone
import requests


class GitLabAPI:
    def __init__(self, domain: str, token: str, project_id: str):
        self.base_url = f"https://{domain}/api/v4"
        self.headers = {"PRIVATE-TOKEN": token}
        if project_id.isdigit():
            self.project_id = project_id
        else:
            self.project_id = urllib.parse.quote(project_id, safe="")

    def _request(self, method: str, endpoint: str, **kwargs) -> requests.Response:
        url = f"{self.base_url}{endpoint}"
        response = requests.request(method, url, headers=self.headers, **kwargs)

        if not response.ok:
            print(f"ERROR: {method} {url} returned {response.status_code}")
            print(f"Response body: {response.text}")
            sys.exit(1)

        return response

    def get_file(self, file_path: str, branch: str) -> str:
        encoded_path = urllib.parse.quote(file_path, safe="")
        endpoint = f"/projects/{self.project_id}/repository/files/{encoded_path}"
        response = self._request("GET", endpoint, params={"ref": branch})
        data = response.json()

        content = base64.b64decode(data["content"]).decode("utf-8")
        last_commit_id = data.get("last_commit_id", "")

        print(f"Fetched '{file_path}' from branch '{branch}' (last commit: {last_commit_id[:8]})")
        return content

    def create_branch(self, new_branch_name: str, src_branch_name: str) -> dict:
        endpoint = f"/projects/{self.project_id}/repository/branches"
        payload = {
            "branch": new_branch_name,
            "ref": src_branch_name,
        }
        response = self._request("POST", endpoint, json=payload)
        print(f"Created branch '{new_branch_name}' from '{src_branch_name}'.")
        return response.json()

    def commit_file(self, file_path: str, branch: str, content: str, commit_message: str) -> dict:
        encoded_path = urllib.parse.quote(file_path, safe="")
        endpoint = f"/projects/{self.project_id}/repository/files/{encoded_path}"
        payload = {
            "branch": branch,
            "content": content,
            "commit_message": commit_message,
        }
        response = self._request("PUT", endpoint, json=payload)
        print(f"Committed update to '{file_path}' on branch '{branch}'.")
        return response.json()

    def create_merge_request(self, source_branch: str, target_branch: str, title: str, description: str = "") -> dict:
        endpoint = f"/projects/{self.project_id}/merge_requests"
        payload = {
            "source_branch": source_branch,
            "target_branch": target_branch,
            "title": title,
            "description": description,
            "remove_source_branch": True,
        }
        response = self._request("POST", endpoint, json=payload)
        data = response.json()
        print(f"Created Merge Request {data['iid']}: {data['web_url']}")
        return data



def get_version_from_branch(branch_name: str) -> str:
    match = re.search(r"release/(\d+\.\d+\.\d+)", branch_name)
    if not match:
        raise ValueError(f"Could not extract version from branch name: {branch_name}")
    return match.group(1)

def compute_checksum(file_path: str, algorithm: str) -> str:
    hash_func = hashlib.new(algorithm)
    with open(file_path, "rb") as f:
        for chunk in iter(lambda: f.read(8192), b""):
            hash_func.update(chunk)
    return hash_func.hexdigest()

# architecture: 'x64' or 'arm64'
def generate_release_json(version: str, architecture: str) -> str:
    file_path = f"/builds/ProtonVPN/Windows/win-app/Setup/Installers/ProtonVPN_v{version}_{architecture}.exe"

    if not os.path.isfile(file_path):
        raise FileNotFoundError(f"File not found: {file_path}")

    sha256 = compute_checksum(file_path, "sha256")
    sha512 = compute_checksum(file_path, "sha512")

    current_date_iso = datetime.now(timezone.utc).strftime("%Y-%m-%d")

    release_json = (
         '{\n'
        f'    "Version": "{version}",\n'
         '    "CategoryName": "EarlyAccess",\n'
         '    "File": {\n'
        f'        "Url": "https://vpn.protondownload.com/download/ProtonVPN_v{version}_{architecture}.exe",\n'
        f'        "SHA256CheckSum": "{sha256}",\n'
        f'        "SHA512CheckSum": "{sha512}",\n'
         '        "Args": "/silent"\n'
         '    },\n'
         '    "ReleaseNotes": [\n'
         '        {\n'
         '            "Notes": [\n'
         '                "",\n'
         '                ""\n'
         '            ]\n'
         '        }\n'
         '    ],\n'
        f'    "ReleaseDate": "{current_date_iso}T06:00:00Z",\n'
         '    "SystemVersion": {\n'
         '        "Minimum": "10.0.19041"\n'
         '    },\n'
         '    "RolloutProportion": 1.00\n'
         '}'
    )

    return release_json

def get_env(name: str) -> str:
    value = os.environ.get(name, "").strip()
    if not value:
        print(f"ERROR: Required environment variable '{name}' is not set or empty.")
        sys.exit(1)
    return value

def map_new_release_object(new_release_json_str: str) -> dict:
    try:
        return json.loads(new_release_json_str)
    except json.JSONDecodeError as e:
        print(f"ERROR: Failed to parse new JSON string: {e}")
        sys.exit(1)

def validate_release_object(release: dict) -> None:
    required_keys = ["Version", "CategoryName", "File", "ReleaseNotes", "ReleaseDate", "SystemVersion", "RolloutProportion"]
    missing = [k for k in required_keys if k not in release]
    if missing:
        print(f"ERROR: Release object is missing required keys: {missing}")
        sys.exit(1)

    required_file_keys = ["Url", "SHA256CheckSum", "SHA512CheckSum", "Args"]
    missing = [k for k in required_file_keys if k not in release.get("File", {})]
    if missing:
        print(f"ERROR: Release object File is missing required keys: {missing}")
        sys.exit(1)

    print(f"Validated release object for version: {release['Version']}")

def generate_new_release(version: str, architecture: str) -> dict:
    new_release_json_str = generate_release_json(version=version, architecture=architecture)
    print("Generated JSON:")
    print(new_release_json_str)
    new_release = map_new_release_object(new_release_json_str)
    validate_release_object(new_release)
    return new_release

def load_target_file_data(api: GitLabAPI, target_json_path: str, target_branch: str):
    target_file_str = api.get_file(file_path=target_json_path, branch=target_branch)

    try:
        target_file_data = json.loads(target_file_str)
    except json.JSONDecodeError as e:
        print(f"ERROR: Failed to parse the JSON file in the deployment repo: {e}")
        sys.exit(1)
        
    # Validate the JSON file
    if "Releases" not in target_file_data or not isinstance(target_file_data["Releases"], list):
        print("ERROR: JSON file in the deployment repo does not contain a 'Releases' array.")
        sys.exit(1)

    return target_file_data

def check_if_version_already_exists(version: str, target_file_data):
    existing_versions = [r.get("Version") for r in target_file_data["Releases"]]
    if version in existing_versions:
        print(f"ERROR: Version '{version}' already exists in the 'Releases' array.")
        sys.exit(1)

def format_rollout_proportions(json_str: str) -> str:
    return re.sub(r'("RolloutProportion":\s*)(\d+\.?\d*)',
        lambda m: f'{m.group(1)}{float(m.group(2)):.2f}',
        json_str
    )

def generate_target_file_data_str(target_file_data) -> str:
    # Serialize with consistent formatting (4-space indent, no trailing whitespace)
    target_file_data_str = json.dumps(target_file_data, indent=4, ensure_ascii=False)

    # Format RolloutProportion values to always have two decimal places
    target_file_data_str = format_rollout_proportions(target_file_data_str)

    # Ensure file ends with a newline
    if not target_file_data_str.endswith("\n"):
        target_file_data_str += "\n"

    return target_file_data_str

def deploy_file(api: GitLabAPI, architecture: str, version: str, target_json_path: str, target_branch: str, new_branch_name: str):
    new_release = generate_new_release(version=version, architecture=architecture)

    print(f"Fetching '{target_json_path}' from '{target_branch}' branch in deployment repo")
    target_file_data = load_target_file_data(api, target_json_path, target_branch)

    print(f"Current number of releases: {len(target_file_data['Releases'])}")
    if len(target_file_data['Releases']) > 0:
        print(f"- Latest release version: {target_file_data['Releases'][0]['Version']}")

    check_if_version_already_exists(version, target_file_data)

    print(f"Prepending version {version}")
    target_file_data["Releases"].insert(0, new_release)
    print(f"New number of releases: {len(target_file_data['Releases'])}")
    print(f"- Latest release version: {target_file_data['Releases'][0]['Version']}")

    target_file_data_str = generate_target_file_data_str(target_file_data)
    print("New target file content:")
    print(f"{target_file_data_str}")

    commit_message = f"Release {version} {architecture} to beta"
    print(f"Committing changes with message '{commit_message}'")
    api.commit_file(target_json_path, new_branch_name, target_file_data_str, commit_message)



# Start of v2 file code (windows-releases.json)

def generate_v2_release_json(version: str) -> str:
    file_path = f"/builds/ProtonVPN/Windows/win-app/Setup/Installers/ProtonVPN_v{version}_x64.exe"

    if not os.path.isfile(file_path):
        raise FileNotFoundError(f"File not found: {file_path}")

    sha256 = compute_checksum(file_path, "sha256")
    sha512 = compute_checksum(file_path, "sha512")

    current_date_iso = datetime.now(timezone.utc).strftime("%Y-%m-%d")

    release_json = (
         '{\n'
        f'    "Version": "{version}",\n'
         '    "File": {\n'
        f'        "Url": "https://vpn.protondownload.com/download/ProtonVPN_v{version}_x64.exe",\n'
        f'        "SHA256CheckSum": "{sha256}",\n'
        f'        "SHA512CheckSum": "{sha512}",\n'
         '        "Arguments": "/silent"\n'
         '    },\n'
         '    "ChangeLog": [\n'
         '        "",\n'
         '        ""\n'
         '    ],\n'
        f'    "ReleaseDate": "{current_date_iso}T06:00:00Z",\n'
         '    "MinimumOsVersion": "10.0.19041",\n'
         '    "RolloutPercentage": 100\n'
         '}'
    )

    return release_json

def validate_v2_release_object(release: dict) -> None:
    required_keys = ["Version", "File", "ChangeLog", "ReleaseDate", "MinimumOsVersion", "RolloutPercentage"]
    missing = [k for k in required_keys if k not in release]
    if missing:
        print(f"ERROR: Release object is missing required keys: {missing}")
        sys.exit(1)

    required_file_keys = ["Url", "SHA256CheckSum", "SHA512CheckSum", "Arguments"]
    missing = [k for k in required_file_keys if k not in release.get("File", {})]
    if missing:
        print(f"ERROR: Release object File is missing required keys: {missing}")
        sys.exit(1)

    print(f"Validated release object for version: {release['Version']}")

def generate_new_v2_file_release(version: str) -> dict:
    new_release_json_str = generate_v2_release_json(version=version)
    print("Generated JSON:")
    print(new_release_json_str)
    new_release = map_new_release_object(new_release_json_str)
    validate_v2_release_object(new_release)
    return new_release

def load_target_v2_file_data(api: GitLabAPI, target_json_path: str, target_branch: str):
    target_file_str = api.get_file(file_path=target_json_path, branch=target_branch)

    try:
        target_file_data = json.loads(target_file_str)
    except json.JSONDecodeError as e:
        print(f"ERROR: Failed to parse the JSON file in the deployment repo: {e}")
        sys.exit(1)
        
    # Validate the JSON file
    if "Categories" not in target_file_data or not isinstance(target_file_data["Categories"], list):
        print("ERROR: JSON file in the deployment repo does not contain a 'Categories' array.")
        sys.exit(1)
        
    if len(target_file_data['Categories']) < 1:
        print(f"ERROR: JSON file in the deployment repo contains a 'Categories' array with no categories (EarlyAccess/Stable).")
        sys.exit(1)

    for category in target_file_data['Categories']:
        if "Releases" not in category or not isinstance(category["Releases"], list):
            print(f"ERROR: A category is missing the 'Releases' array.")
            sys.exit(1)

    return target_file_data

def check_if_v2_version_already_exists(version: str, target_file_data):
    for category in target_file_data['Categories']:
        existing_versions = [r.get("Version") for r in category["Releases"]]
        if version in existing_versions:
            print(f"ERROR: Version '{version}' already exists in the '{category['Name']}' category.")
            sys.exit(1)

def deploy_v2_file(api: GitLabAPI, version: str, target_json_path: str, target_branch: str, new_branch_name: str):
    new_release = generate_new_v2_file_release(version=version)

    print(f"Fetching '{target_json_path}' from '{target_branch}' branch in deployment repo")
    target_file_data = load_target_v2_file_data(api, target_json_path, target_branch)

    print(f"Current number of categories: {len(target_file_data['Categories'])}")
    for category in target_file_data['Categories']:
        print(f"- Category: {category['Name']}, Number of releases: {len(category['Releases'])}")
        if len(category['Releases']) > 0:
            print(f"--- Latest release version: {category['Releases'][0]['Version']}")

    check_if_v2_version_already_exists(version, target_file_data)

    print(f"Prepending version {version}")
    target_file_data["Categories"][0]["Releases"].insert(0, new_release)

    print(f"New number of categories: {len(target_file_data['Categories'])}")
    for category in target_file_data['Categories']:
        print(f"- Category: {category['Name']}, Number of releases: {len(category['Releases'])}")
        if len(category['Releases']) > 0:
            print(f"--- Latest release version: {category['Releases'][0]['Version']}")

    target_file_data_str = generate_target_file_data_str(target_file_data)
    print("New target file content:")
    print(f"{target_file_data_str}")

    commit_message = f"Release {version} x64 to beta (windows-releases.json)"
    print(f"Committing changes with message '{commit_message}'")
    api.commit_file(target_json_path, new_branch_name, target_file_data_str, commit_message)

# End of v2 file code (windows-releases.json)



print("Loading necessary environment variables")
branch_name = get_env("CI_COMMIT_BRANCH")
print(f"Branch name: {branch_name}")
target_repository_domain = get_env("DEPLOY_REPO_DOMAIN")
target_repository_token = get_env("DEPLOY_PAT")
target_repository_id = get_env("DEPLOY_REPO_ID")
target_branch = "master"

api = GitLabAPI(target_repository_domain, target_repository_token, target_repository_id)

version = get_version_from_branch(branch_name)
new_branch_name = f"release/{version}"
print(f"Creating branch '{new_branch_name}'")
api.create_branch(new_branch_name=new_branch_name, src_branch_name=target_branch)

print(f"\n\nGenerating new x64/v1/version.json")
deploy_file(api, architecture="x64", version=version, target_json_path="x64/v1/version.json", target_branch=target_branch, new_branch_name=new_branch_name)

print(f"\n\nGenerating new arm64/v1/version.json")
deploy_file(api, architecture="arm64", version=version, target_json_path="arm64/v1/version.json", target_branch=target_branch, new_branch_name=new_branch_name)

print(f"\n\nGenerating new windows-releases.json")
deploy_v2_file(api, version=version, target_json_path="windows-releases.json", target_branch=target_branch, new_branch_name=new_branch_name)

merge_request_title = f"Release {version} to beta"
merge_request_description = f"Automated release deployment of version {version}"
print(f"\n\nCreating Merge Request with title '{merge_request_title}'")
merge_request = api.create_merge_request(
    source_branch=new_branch_name,
    target_branch=target_branch,
    title=merge_request_title,
    description=merge_request_description
)

print("Successfully created merge request in deployment repository")
print(f"Merge request {merge_request['iid']}: {merge_request['web_url']}")
