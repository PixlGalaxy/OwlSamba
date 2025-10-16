import win32evtlog
import json
from datetime import datetime
import ipaddress
import os
import subprocess
import socket
from colorama import init, Fore, Style

# Initialize colorama
init(autoreset=True)

# Configuration
AutoExit = True                                         # Set the app to close automatically when the process is complete
LOG_NAME = "Security"                                   # Name of the log to analyze
EVENT_ID = 4625                                         # Event ID for failed login attempts
JSON_FILE = "banned_ips.json"                           # File to store banned IPs
WHITELIST_IPS = ["127.0.0.1", "192.168.0.0/24"]         # Subnets or IPs to whitelist
WHITELIST_DOMAINS = ["example.com"]                     # Domains to resolve and add to the whitelist
BanIPs = True                                           # Enable/Disable banning IPs in the Firewall
THRESHOLD = 10                                          # Number of attempts before banning an IP
whitelistlog = []                                       # List to store detected whitelisted IPs for log

# Load banned IPs from the JSON file
def load_banned_ips():

    if not os.path.exists(JSON_FILE):
        return {}
    
    with open(JSON_FILE, "r", encoding="utf-8") as f:
        return json.load(f)

# Save updated banned IPs to the JSON file
def save_banned_ips(banned_ips):

    with open(JSON_FILE, "w", encoding="utf-8") as f:
        json.dump(banned_ips, f, indent=4)

# Resolve domains and add their IPs to the whitelist
def resolve_whitelist_domains():
    print("-" * 130)

    for domain in WHITELIST_DOMAINS:

        try:
            ip = socket.gethostbyname(domain)

            if ip not in WHITELIST_IPS:
                WHITELIST_IPS.append(ip)
                print(f"{Fore.GREEN}[INFO]{Style.RESET_ALL} Domain '{domain}' resolved to '{ip}' and added to the whitelist.")

        except Exception as e:
            print(f"{Fore.RED}[ERROR]{Style.RESET_ALL} Error resolving domain '{domain}': {e}")

# Check if an IP is in the whitelist
def is_whitelisted(ip_address):
    if ip_address == "-":
        return True  # Skip invalid IPs like "-"

    try:
        ip_obj = ipaddress.ip_address(ip_address)
        for whitelisted in WHITELIST_IPS:

            if '/' in whitelisted:
                if ip_obj in ipaddress.ip_network(whitelisted, strict=False):
                    return True
                
            elif ip_address == whitelisted:
                return True
            
    except ValueError:
        print(f"{Fore.RED}[ERROR]{Style.RESET_ALL} Invalid IP detected: {ip_address}")
    return False

# Add an IP to the Windows Firewall
def add_to_firewall(ip_address):

    if not BanIPs:
        print(f"{Fore.YELLOW}[DEBUG]{Style.RESET_ALL} BanIPs is disabled. IP {ip_address} was not blocked.")
        return
    
    try:
        rule_name = f"SMB_block_V2_{ip_address}"  # Custom rule name
        subprocess.run(
            ["netsh", "advfirewall", "firewall", "add", "rule", 
             f"name={rule_name}", f"dir=in", "action=block", f"remoteip={ip_address}"],
            check=True
        )

        print(f"{Fore.GREEN}[FIREWALL]{Style.RESET_ALL} IP {ip_address} blocked in the firewall with rule '{rule_name}'.")
    except subprocess.CalledProcessError as e:
        print(f"{Fore.RED}[ERROR]{Style.RESET_ALL} Error blocking IP {ip_address}: {e}")

# Process an event to extract necessary details
def process_event(event):

    try:
        time_generated = event.TimeGenerated.Format()
        ip_address = None
        workstation = "-"
        user = "-"

        if event.StringInserts:
            ip_address = event.StringInserts[-2]
            workstation = event.StringInserts[13]
            user = event.StringInserts[5]

        if ip_address and not is_whitelisted(ip_address):
            return ip_address, {
                "TimeGenerated": time_generated,
                "Workstation": workstation,
                "User": user
            }
        
        elif ip_address:
            WhitelistedIPs(ip_address, False)

    except Exception as e:
        print(f"{Fore.RED}[ERROR]{Style.RESET_ALL} Error processing event: {e}")
    return None

# Store whitelisted IPs for logging
def WhitelistedIPs(ip, display_logs):
    global whitelistlog

    if not display_logs:
        if ip not in whitelistlog:
            whitelistlog.append(ip)

    elif display_logs:
        for ip in whitelistlog:
            print(f"{Fore.YELLOW}[WARN]{Style.RESET_ALL} IP {ip} skipped: Whitelisted.")

# Fetch events from the Windows Event Log
def fetch_events():
    server = None
    log_type = win32evtlog.OpenEventLog(server, LOG_NAME)
    events = []

    try:
        flags = win32evtlog.EVENTLOG_FORWARDS_READ | win32evtlog.EVENTLOG_SEQUENTIAL_READ

        while True:
            batch = win32evtlog.ReadEventLog(log_type, flags, 0)
            if not batch:
                break
            events.extend([event for event in batch if event.EventID == EVENT_ID])

    finally:
        win32evtlog.CloseEventLog(log_type)
    return events

# Main
if __name__ == "__main__":
    # Resolve domains and add their IPs to the whitelist
    resolve_whitelist_domains()

    # Load previously banned IPs
    banned_ips = load_banned_ips()
    print("-" * 130)
    print(f"{Fore.GREEN}[MAIN THREAD]{Style.RESET_ALL} Starting event extraction...")
    events = fetch_events()
    print(f"{Fore.GREEN}[MAIN THREAD]{Style.RESET_ALL} Events loaded: {len(events)}")
    print("-" * 130)
    print(f"{Fore.GREEN}[INFO]{Style.RESET_ALL} IPs Banned:")

    # Process events
    for event in events:
        result = process_event(event)
        if result:
            ip_address, details = result
            event_time = datetime.strptime(details["TimeGenerated"], "%a %b %d %H:%M:%S %Y")

            # Increment attempts only if the event is more recent
            if ip_address in banned_ips:
                banned_ip_data = banned_ips[ip_address]
                last_attempt = datetime.strptime(banned_ip_data.get("LastAttempt", "1970-01-01 00:00:00"), "%a %b %d %H:%M:%S %Y")
                if event_time > last_attempt:
                    attempts = banned_ip_data.get("Attempts", 0) + 1
                    banned_ip_data.update({
                        "Attempts": attempts,
                        "LastAttempt": details["TimeGenerated"],
                        "Workstation": details["Workstation"],
                        "LastUser": details["User"]
                    })

                    if attempts >= THRESHOLD and not banned_ip_data.get("Banned", False):
                        print(f"{Fore.GREEN}[INFO]{Style.RESET_ALL} IP {ip_address} reached the threshold ({THRESHOLD}) with {attempts} attempts and will be blocked.")
                        add_to_firewall(ip_address)
                        banned_ip_data["Banned"] = True
                        banned_ip_data["BannedTime"] = datetime.now().strftime("%Y-%m-%d %H:%M:%S")

            else:
                # If it is a new IP, initialize
                banned_ips[ip_address] = {
                    "Attempts": 1,
                    "LastAttempt": details["TimeGenerated"],
                    "Workstation": details["Workstation"],
                    "LastUser": details["User"],
                    "Banned": False
                }

    print("-" * 130)
    WhitelistedIPs(ip_address, True)

    # Save updated banned IPs to the JSON file
    save_banned_ips(banned_ips)

    print("-" * 130)
    print(f"{Fore.GREEN}[INFO]{Style.RESET_ALL} SMB IP Blocker V2 processing completed.")
    if not AutoExit:
        print(f"{Fore.GREEN}[INFO]{Style.RESET_ALL} Press Any Key To Exit.")
        os.system("pause >nul")
