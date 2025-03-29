#!/usr/bin/env python
import os
import json
import re
import random
import sys
from datetime import datetime

def extract_issues(log_dir):
    """Extract timeout and error events from logs from Feb 2025 onwards."""
    timeouts = []
    errors = []
    
    # Find all SMS log files and filter for Feb 2025 onwards (20250201 or greater)
    all_logs = [f for f in os.listdir(log_dir) if f.startswith('SMS_Log_')]
    recent_logs = [f for f in all_logs if f >= 'SMS_Log_20250201.log']
    
    print(f"Found {len(all_logs)} total log files")
    print(f"Using {len(recent_logs)} log files from February 2025 onwards")
    
    # Process each log file
    for log_file in sorted(recent_logs):
        file_path = os.path.join(log_dir, log_file)
        
        # Extract date from filename
        date_match = re.search(r'SMS_Log_(\d{8})\.log', log_file)
        if date_match:
            log_date = date_match.group(1)
        else:
            continue  # Skip files that don't match the expected pattern
        
        with open(file_path, 'r', encoding='utf-8', errors='ignore') as f:
            for line_num, line in enumerate(f, 1):
                line = line.strip()
                if not line:
                    continue
                    
                try:
                    # Parse the JSON log entry
                    log_entry = json.loads(line)
                    
                    # Check for timeout events
                    if ('Timeout' in log_entry.get('EventType', '') or 
                        'timeout' in log_entry.get('Details', '').lower()):
                        timeouts.append({
                            'log_file': log_file,
                            'line_num': line_num,
                            'raw_data': line,
                            'date': log_date
                        })
                    
                    # Check for error events (excluding timeouts)
                    elif ((log_entry.get('Level') == 'ERROR' or 
                          'failed' in log_entry.get('Details', '').lower() or
                          'Failed' in log_entry.get('Details', '') or
                          'fail' in log_entry.get('EventType', '').lower()) and
                          'Timeout' not in log_entry.get('EventType', '')):
                        
                        # Extract phone number if available (for information only, not for filename)
                        phone_match = re.search(r'Number: (\+?\d+)|PhoneNumber: (\+?\d+)', 
                                              log_entry.get('Details', ''))
                        # Also try to extract phone numbers without the "Number:" or "PhoneNumber:" prefix
                        if not phone_match:
                            phone_match = re.search(r'SMS to (\+?\d+)', log_entry.get('Details', ''))
                        
                        phone = None
                        if phone_match:
                            for group in phone_match.groups():
                                if group:
                                    phone = group
                                    break
                        
                        if not phone:
                            phone = 'Unknown'
                            
                        # Normalize phone numbers (strip '+' for consistent comparison)
                        if phone != 'Unknown':
                            phone = phone.lstrip('+')
                        
                        errors.append({
                            'log_file': log_file,
                            'line_num': line_num,
                            'raw_data': line,
                            'phone': phone,
                            'date': log_date
                        })
                        
                except (json.JSONDecodeError, KeyError):
                    continue  # Skip malformed lines
    
    return timeouts, errors

def write_raw_data_file(issue, issue_type, index, output_dir):
    """Write a file containing just the raw data for the issue."""
    # Simplified filenames as requested
    filename = f"analysis_{issue_type}_{index+1}.txt"
    
    # Full path including output directory
    filepath = os.path.join(output_dir, filename)
    
    # Write the file
    with open(filepath, 'w', encoding='utf-8') as f:
        f.write(f"# Raw Data: SMS {issue_type.title()} (Feb 2025+)\n\n")
        f.write(f"## Log File: {issue['log_file']}\n")
        f.write(f"## Date: {issue['date']}\n")
        f.write(f"## Line Number: {issue['line_num']}\n")
        if 'phone' in issue and issue['phone'] != 'Unknown':
            # Add the '+' back if it's a full phone number (not 'Unknown')
            display_phone = issue['phone']
            if display_phone.isdigit() and len(display_phone) > 7:
                display_phone = '+' + display_phone
            f.write(f"## Phone Number: {display_phone}\n")
        f.write("\n```\n")
        f.write(issue['raw_data'])
        f.write("\n```\n")
    
    print(f"Created file: {filepath}")
    return filename

def main():
    if len(sys.argv) < 2:
        print("Usage: python sample_sms_issues.py <log_dir>")
        sys.exit(1)
    
    log_dir = sys.argv[1]
    output_dir = "analysis"  # Use the analysis directory
    
    if not os.path.isdir(log_dir):
        print(f"Error: Log directory '{log_dir}' not found")
        sys.exit(1)
    
    # Ensure the output directory exists
    if not os.path.exists(output_dir):
        os.makedirs(output_dir)
        print(f"Created output directory: {output_dir}")
    else:
        # Clean any existing analysis files
        for f in os.listdir(output_dir):
            if f.startswith("analysis_") and f.endswith(".txt"):
                os.remove(os.path.join(output_dir, f))
        print(f"Cleaned existing analysis files from {output_dir}")
    
    print(f"Extracting issues from Feb 2025+ logs in: {log_dir}")
    timeouts, errors = extract_issues(log_dir)
    
    print(f"Found {len(timeouts)} timeout events and {len(errors)} error events from Feb 2025 onwards")
    
    # Sample timeout issues randomly
    sampled_timeouts = random.sample(timeouts, min(5, len(timeouts)))
    
    # Artificially duplicate some items if we don't have enough timeouts
    while len(sampled_timeouts) < 5 and len(timeouts) > 0:
        sampled_timeouts.append(random.choice(timeouts))
    
    # Sample error issues with unique phone numbers
    sampled_errors = []
    
    # Group errors by phone number (normalized without '+' prefix)
    errors_by_phone = {}
    for error in errors:
        phone = error.get('phone', 'Unknown')
        if phone not in errors_by_phone:
            errors_by_phone[phone] = []
        errors_by_phone[phone].append(error)
    
    # Get list of available phone numbers (excluding Unknown)
    available_phones = [phone for phone in list(errors_by_phone.keys()) if phone != 'Unknown']
    
    # Shuffle to randomize selection
    random.shuffle(available_phones)
    
    # Select up to 5 errors with different phone numbers
    selected_phones = set()
    
    # First pass: select errors with known phone numbers
    for phone in available_phones:
        if len(sampled_errors) >= 5:
            break
            
        if phone not in selected_phones:
            error = random.choice(errors_by_phone[phone])
            sampled_errors.append(error)
            selected_phones.add(phone)
    
    # If we still don't have 5, add errors with unknown phone numbers
    if len(sampled_errors) < 5 and 'Unknown' in errors_by_phone:
        unknown_errors = errors_by_phone['Unknown']
        if unknown_errors:
            needed = min(5 - len(sampled_errors), len(unknown_errors))
            additional_unknowns = random.sample(unknown_errors, needed)
            for error in additional_unknowns:
                if len(sampled_errors) < 5:
                    sampled_errors.append(error)
    
    # Check for duplicated phone numbers and ensure each error has a unique phone number
    used_phone_numbers = set()
    final_sampled_errors = []
    
    for error in sampled_errors:
        phone = error.get('phone', 'Unknown')
        if phone != 'Unknown' and phone not in used_phone_numbers:
            final_sampled_errors.append(error)
            used_phone_numbers.add(phone)
        elif phone == 'Unknown' and len(final_sampled_errors) < 5:
            # We can add multiple 'Unknown' phone errors if needed
            final_sampled_errors.append(error)
    
    # Verify we have the correct number of unique phone numbers
    print(f"\nSelected {len(sampled_timeouts)} timeout samples")
    print(f"Selected {len(final_sampled_errors)} error samples with {len(used_phone_numbers)} unique phone numbers")
    
    # Write raw data files
    timeout_files = []
    error_files = []
    
    print("\nGenerating timeout data files:")
    for i, timeout in enumerate(sampled_timeouts):
        timeout_files.append(write_raw_data_file(timeout, "timeout", i, output_dir))
    
    print("\nGenerating error data files:")
    for i, error in enumerate(final_sampled_errors):
        error_files.append(write_raw_data_file(error, "error", i, output_dir))
    
    # Verify file count
    total_files = len(timeout_files) + len(error_files)
    print(f"\nExtraction complete - created {total_files} files with raw SMS issue data in '{output_dir}' directory")
    
    # Double-check the file count
    actual_files = len([f for f in os.listdir(output_dir) if f.startswith("analysis_") and f.endswith(".txt")])
    print(f"Verification: {actual_files} files exist in the directory")

if __name__ == "__main__":
    main() 