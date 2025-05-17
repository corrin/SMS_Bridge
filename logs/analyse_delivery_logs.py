import os
import json
import pandas as pd
from datetime import date
from pathlib import Path
from collections import defaultdict

# --- Config ---
log_dir = Path(r"C:\Users\User\Downloads\od_logs")
log_files = [
    f for f in log_dir.glob("SMS_log*")
    if any(char.isdigit() for char in f.stem)
    and date.fromisoformat(''.join(filter(str.isdigit, f.stem))[:10]) >= date(2025, 3, 1)
]

# --- Helper: Extract fields from SendAttempt ---
def extract_phone_and_message(details: str):
    phone, message = "", ""
    try:
        if "PhoneNumber:" in details:
            parts = details.split("PhoneNumber:")[1].strip()
            phone = parts.split(",")[0].strip()
            if "Message:" in parts:
                message = parts.split("Message:")[1].strip()
            else:
                message = details.split("Message:")[1].strip()
    except Exception:
        pass
    return phone, message

# --- Step 1: Parse logs line-by-line ---
message_statuses = defaultdict(list)
send_attempt_pending = None

for file_path in sorted(log_files):
    with open(file_path, "r", encoding="utf-8") as f:
        for line in f:
            try:
                entry = json.loads(line.strip())
                timestamp = pd.to_datetime(entry["Timestamp"], errors="raise")
                entry["Timestamp_dt"] = timestamp  # Trust logs are already in NZT with tzinfo

                event_type = entry.get("EventType", "")
                msg_id = entry.get("MessageId", "")

                if event_type == "SendAttempt":
                    send_attempt_pending = entry

                elif event_type == "SendSuccess" and msg_id:
                    if send_attempt_pending:
                        phone, message = extract_phone_and_message(send_attempt_pending["Details"])
                        entry["ExtractedPhone"] = phone
                        entry["ExtractedMessage"] = message
                        send_attempt_pending = None
                    else:
                        entry["ExtractedPhone"] = ""
                        entry["ExtractedMessage"] = ""

                if msg_id:
                    message_statuses[msg_id].append(entry)

            except Exception as e:
                raise RuntimeError(f"Error parsing log line: {e}")

# --- Step 2: Summarise all outbound messages ---
message_summaries = []

for msg_id, events in message_statuses.items():
    event_types = [e["EventType"] for e in events]
    if not any(evt in event_types for evt in ("SendAttempt", "SendSuccess", "MessageSent")):
        continue

    timestamps = [e["Timestamp_dt"] for e in events if "Timestamp_dt" in e]
    if not timestamps:
        raise ValueError(f"No valid timestamps found for MessageId {msg_id}")

    first_time = min(timestamps)
    last_time = max(timestamps)
    duration = (last_time - first_time).total_seconds()

    send_success = next((e for e in events if e["EventType"] == "SendSuccess"), {})
    phone = send_success.get("ExtractedPhone", "")
    message = send_success.get("ExtractedMessage", "")

    if any(e["EventType"] == "DeliveryStatus" and "Delivered" in e["Details"] for e in events):
        outcome = "Delivered"
    elif any(e["EventType"] == "DeliveryStatus" and "Failed" in e["Details"] for e in events):
        outcome = "Failed"
    elif "SendSuccess" in event_types or "SendAttempt" in event_types:
        outcome = "Gave up trying"
    else:
        outcome = "Unknown"

    message_summaries.append({
        "MessageId": msg_id,
        "FirstLogTime": first_time,
        "LastLogTime": last_time,
        "DurationSeconds": duration,
        "PhoneNumber": phone,
        "Message": message,
        "UltimateResult": outcome
    })

# --- Step 3: Output CSV ---
df = pd.DataFrame(message_summaries).sort_values(by="FirstLogTime")
df.to_csv("message_summary.csv", index=False)
print("\u2705 Saved message_summary.csv")
