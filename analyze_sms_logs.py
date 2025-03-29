import os
import json
import re
import datetime
import pandas as pd
import matplotlib.pyplot as plt
import seaborn as sns
from pathlib import Path
import numpy as np
import argparse
import sys

def parse_log_file(file_path):
    """Parse a single log file and extract delivery time information."""
    results = []
    timeouts = []
    errors = []
    
    with open(file_path, 'r', encoding='utf-8') as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
                
            try:
                # Parse the JSON log entry
                log_entry = json.loads(line)
                
                # Extract timestamp for all entries
                if 'Timestamp' in log_entry:
                    timestamp = datetime.datetime.fromisoformat(log_entry['Timestamp'].split('+')[0])
                    date = timestamp.date()
                    time_of_day = timestamp.time()
                    hour = timestamp.hour
                    
                    # Extract message ID
                    message_id = log_entry.get('MessageId', '')
                
                # Check if this is a delivery status message with timing info
                if (log_entry.get('EventType') == 'DeliveryStatus' and 
                    'Details' in log_entry and 
                    'Status: Delivered' in log_entry['Details']):
                    
                    # Extract phone number
                    phone_match = re.search(r'Number: (\+?\d+)', log_entry['Details'])
                    phone_number = phone_match.group(1) if phone_match else 'Unknown'
                    
                    # Extract delivery time
                    delivery_time_match = re.search(r'Delivery Time: (\d+\.\d+|\d+)', log_entry['Details'])
                    if delivery_time_match:
                        delivery_time = float(delivery_time_match.group(1))
                        
                        results.append({
                            'timestamp': timestamp,
                            'date': date,
                            'time': time_of_day,
                            'hour': hour,
                            'message_id': message_id,
                            'phone_number': phone_number,
                            'delivery_time': delivery_time
                        })
                
                # Check for timeout messages
                elif 'timeout' in log_entry.get('Details', '').lower() or 'Timeout' in log_entry.get('EventType', ''):
                    provider = log_entry.get('Provider', 'Unknown')
                    details = log_entry.get('Details', '')
                    
                    timeouts.append({
                        'timestamp': timestamp,
                        'date': date,
                        'time': time_of_day,
                        'hour': hour,
                        'message_id': message_id,
                        'provider': provider,
                        'details': details
                    })
                
                # Check for error messages
                elif (log_entry.get('Level') == 'ERROR' or 
                      'error' in log_entry.get('Details', '').lower() or
                      'failed' in log_entry.get('Details', '').lower() or
                      'fail' in log_entry.get('EventType', '').lower()):
                    
                    provider = log_entry.get('Provider', 'Unknown')
                    details = log_entry.get('Details', '')
                    level = log_entry.get('Level', '')
                    event_type = log_entry.get('EventType', '')
                    
                    errors.append({
                        'timestamp': timestamp,
                        'date': date,
                        'time': time_of_day,
                        'hour': hour,
                        'message_id': message_id,
                        'provider': provider,
                        'level': level,
                        'event_type': event_type,
                        'details': details
                    })
                    
            except (json.JSONDecodeError, KeyError, ValueError) as e:
                continue  # Skip malformed lines
                
    return results, timeouts, errors

def find_missing_deliveries(log_dir):
    """Find messages that were sent but have no corresponding delivery status record."""
    print("\n===== MISSING DELIVERY ANALYSIS =====")
    
    # Dictionary to track message IDs
    sent_messages = {}  # message_id -> details
    delivered_messages = set()  # set of message IDs
    
    # Process all log files
    log_files = [f for f in os.listdir(log_dir) if f.startswith('SMS_Log_')]
    
    for log_file in log_files:
        file_path = os.path.join(log_dir, log_file)
        
        with open(file_path, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                    
                try:
                    # Parse JSON log entry
                    log_entry = json.loads(line)
                    
                    # Track sent messages
                    if log_entry.get('EventType') == 'SendSuccess':
                        message_id = log_entry.get('MessageId', '')
                        timestamp = log_entry.get('Timestamp', '')
                        details = log_entry.get('Details', '')
                        
                        # Extract phone number
                        phone_match = re.search(r'PhoneNumber: (\+?\d+)', details)
                        phone = phone_match.group(1) if phone_match else 'Unknown'
                        
                        if message_id:
                            sent_messages[message_id] = {
                                'timestamp': timestamp,
                                'phone': phone,
                                'file': log_file
                            }
                    
                    # Track delivered messages
                    elif log_entry.get('EventType') == 'DeliveryStatus' and 'Status: Delivered' in log_entry.get('Details', ''):
                        message_id = log_entry.get('MessageId', '')
                        if message_id:
                            delivered_messages.add(message_id)
                
                except (json.JSONDecodeError, KeyError):
                    continue
    
    # Find messages sent but not delivered
    missing_deliveries = {msg_id: details for msg_id, details in sent_messages.items() 
                         if msg_id not in delivered_messages}
    
    print(f"Total messages sent: {len(sent_messages)}")
    print(f"Messages with delivery confirmation: {len(delivered_messages)}")
    print(f"Messages missing delivery confirmation: {len(missing_deliveries)} ({100 * len(missing_deliveries) / len(sent_messages):.2f}% of sent messages)")
    
    if missing_deliveries:
        print("\nSample of Messages Missing Delivery Confirmation:")
        sample_size = min(10, len(missing_deliveries))
        sample_ids = list(missing_deliveries.keys())[:sample_size]
        
        for msg_id in sample_ids:
            details = missing_deliveries[msg_id]
            print(f"ID: {msg_id}, Timestamp: {details['timestamp']}, Phone: {details['phone']}, File: {details['file']}")
    
    return missing_deliveries

def analyze_logs(log_dir):
    """Process all log files in the directory and analyze delivery times."""
    all_results = []
    all_timeouts = []
    all_errors = []
    
    # Find all SMS log files
    log_files = [f for f in os.listdir(log_dir) if f.startswith('SMS_Log_')]
    
    if not log_files:
        print(f"No SMS log files found in {log_dir}")
        return None, None, None
    
    print(f"Found {len(log_files)} SMS log files to process")
    
    # Parse each log file
    for log_file in log_files:
        file_path = os.path.join(log_dir, log_file)
        results, timeouts, errors = parse_log_file(file_path)
        
        # Extract date from filename (SMS_Log_YYYYMMDD.log)
        date_match = re.search(r'SMS_Log_(\d{8})', log_file)
        log_date = date_match.group(1) if date_match else "Unknown"
        
        all_results.extend(results)
        all_timeouts.extend(timeouts)
        all_errors.extend(errors)
        
        print(f"Processed {log_file} ({log_date}): {len(results)} delivery records, {len(timeouts)} timeouts, {len(errors)} errors")
    
    # Process delivery times
    if all_results:
        df_deliveries = pd.DataFrame(all_results)
        analyze_delivery_times(df_deliveries)
    else:
        print("No delivery time data found in the logs")
        df_deliveries = None
    
    # Process timeouts
    if all_timeouts:
        df_timeouts = pd.DataFrame(all_timeouts)
        analyze_timeouts(df_timeouts)
    else:
        print("No timeouts found in the logs")
        df_timeouts = None
    
    # Process errors
    if all_errors:
        df_errors = pd.DataFrame(all_errors)
        analyze_errors(df_errors)
    else:
        print("No errors found in the logs")
        df_errors = None
    
    return df_deliveries, df_timeouts, df_errors

def analyze_delivery_times(df):
    """Analyze the distribution of delivery times."""
    print("\n===== DELIVERY TIME ANALYSIS =====")
    print(f"Total messages analyzed: {len(df)}")
    
    # Basic statistics
    print("\nDelivery Time Statistics:")
    print(f"Mean delivery time: {df['delivery_time'].mean():.2f} seconds")
    print(f"Median delivery time: {df['delivery_time'].median():.2f} seconds")
    print(f"Min delivery time: {df['delivery_time'].min():.2f} seconds")
    print(f"Max delivery time: {df['delivery_time'].max():.2f} seconds")
    print(f"Standard deviation: {df['delivery_time'].std():.2f} seconds")
    print(f"95th percentile: {df['delivery_time'].quantile(0.95):.2f} seconds")
    print(f"99th percentile: {df['delivery_time'].quantile(0.99):.2f} seconds")
    
    # Analyze distribution
    print("\nDistribution of Delivery Times:")
    bins = [0, 1, 2, 3, 4, 5, 10, 30, 60, 120, float('inf')]
    labels = ['<1s', '1-2s', '2-3s', '3-4s', '4-5s', '5-10s', '10-30s', '30-60s', '1-2m', '>2m']
    df['time_bucket'] = pd.cut(df['delivery_time'], bins=bins, labels=labels)
    distribution = df['time_bucket'].value_counts().sort_index()
    for bucket, count in distribution.items():
        percentage = 100 * count / len(df)
        print(f"{bucket}: {count} messages ({percentage:.2f}%)")
    
    # Analyze by hour of day
    print("\nDelivery Times by Hour of Day:")
    hour_stats = df.groupby('hour')['delivery_time'].agg(['mean', 'median', 'min', 'max', 'count', 'std'])
    print(hour_stats)
    
    # Analyze by date
    print("\nDelivery Times by Date:")
    date_stats = df.groupby('date')['delivery_time'].agg(['mean', 'median', 'min', 'max', 'count', 'std'])
    print(date_stats)
    
    # Analyze tail of distribution (slow deliveries)
    tail_threshold = df['delivery_time'].quantile(0.95)
    tail_df = df[df['delivery_time'] >= tail_threshold]
    
    print(f"\n===== SLOW DELIVERY ANALYSIS (>= {tail_threshold:.2f} seconds) =====")
    print(f"Number of slow deliveries: {len(tail_df)} ({100 * len(tail_df) / len(df):.2f}% of total)")
    
    if len(tail_df) > 0:
        # Patterns in the tail by hour
        print("\nSlow Deliveries by Hour:")
        tail_hour_counts = tail_df['hour'].value_counts().sort_index()
        total_hour_counts = df['hour'].value_counts().sort_index()
        
        for hour in sorted(tail_hour_counts.index):
            if hour in total_hour_counts:
                percentage = 100 * tail_hour_counts[hour] / total_hour_counts[hour]
                print(f"Hour {hour}: {tail_hour_counts[hour]} slow deliveries out of {total_hour_counts[hour]} total ({percentage:.2f}%)")
        
        # Patterns in the tail by date
        print("\nSlow Deliveries by Date:")
        tail_date_counts = tail_df['date'].value_counts().sort_index()
        total_date_counts = df['date'].value_counts().sort_index()
        
        for date in sorted(tail_date_counts.index):
            if date in total_date_counts:
                percentage = 100 * tail_date_counts[date] / total_date_counts[date]
                print(f"Date {date}: {tail_date_counts[date]} slow deliveries out of {total_date_counts[date]} total ({percentage:.2f}%)")
        
        # Check if specific phone numbers have consistently slow deliveries
        print("\nPhone Numbers with Multiple Slow Deliveries:")
        phone_counts = tail_df['phone_number'].value_counts()
        multiple_slow = phone_counts[phone_counts > 1]
        for phone, count in multiple_slow.items():
            percentage = 100 * count / len(tail_df)
            avg_time = tail_df[tail_df['phone_number'] == phone]['delivery_time'].mean()
            print(f"Phone {phone}: {count} slow deliveries ({percentage:.2f}% of slow deliveries), avg time: {avg_time:.2f}s")
    
    # Create visualizations
    plt.figure(figsize=(10, 6))
    sns.histplot(df['delivery_time'], bins=30, kde=True)
    plt.title('Distribution of SMS Delivery Times')
    plt.xlabel('Delivery Time (seconds)')
    plt.ylabel('Count of Messages')
    plt.savefig('delivery_time_distribution.png')
    
    # Log-scale histogram to better visualize the tail
    plt.figure(figsize=(10, 6))
    sns.histplot(df['delivery_time'], bins=30, kde=True, log_scale=(False, True))
    plt.title('Distribution of SMS Delivery Times (Log Scale)')
    plt.xlabel('Delivery Time (seconds)')
    plt.ylabel('Count of Messages (log scale)')
    plt.savefig('delivery_time_distribution_log.png')
    
    # Box plot by hour
    plt.figure(figsize=(14, 8))
    sns.boxplot(x='hour', y='delivery_time', data=df)
    plt.title('Delivery Times by Hour of Day')
    plt.xlabel('Hour of Day')
    plt.ylabel('Delivery Time (seconds)')
    plt.savefig('delivery_time_by_hour.png')
    
    # Heatmap of delivery times by hour and date
    if len(df['date'].unique()) > 1:
        pivot = df.pivot_table(
            index='date', 
            columns='hour', 
            values='delivery_time', 
            aggfunc='median'
        )
        
        plt.figure(figsize=(16, 10))
        sns.heatmap(pivot, annot=True, fmt=".1f", cmap="YlGnBu")
        plt.title('Median Delivery Time by Date and Hour')
        plt.ylabel('Date')
        plt.xlabel('Hour of Day')
        plt.savefig('delivery_time_heatmap.png')

def analyze_timeouts(df):
    """Analyze timeout messages."""
    print("\n===== TIMEOUT ANALYSIS =====")
    print(f"Total timeouts found: {len(df)}")
    
    # Timeouts by provider
    print("\nTimeouts by Provider:")
    provider_counts = df['provider'].value_counts()
    for provider, count in provider_counts.items():
        percentage = 100 * count / len(df)
        print(f"{provider}: {count} timeouts ({percentage:.2f}%)")
    
    # Timeouts by hour
    print("\nTimeouts by Hour of Day:")
    hour_counts = df['hour'].value_counts().sort_index()
    for hour, count in hour_counts.items():
        percentage = 100 * count / len(df)
        print(f"Hour {hour}: {count} timeouts ({percentage:.2f}%)")
    
    # Timeouts by date
    print("\nTimeouts by Date:")
    date_counts = df['date'].value_counts().sort_index()
    for date, count in date_counts.items():
        percentage = 100 * count / len(df)
        print(f"Date {date}: {count} timeouts ({percentage:.2f}%)")
    
    # Sample of timeout messages
    print("\nSample Timeout Messages:")
    samples = df.sample(min(5, len(df)))
    for _, row in samples.iterrows():
        print(f"[{row['timestamp']}] {row['details']}")
    
    # Visualize timeouts by hour
    plt.figure(figsize=(12, 6))
    sns.countplot(x='hour', data=df)
    plt.title('Timeouts by Hour of Day')
    plt.xlabel('Hour of Day')
    plt.ylabel('Number of Timeouts')
    plt.savefig('timeouts_by_hour.png')

def analyze_errors(df):
    """Analyze error messages."""
    print("\n===== ERROR ANALYSIS =====")
    print(f"Total errors found: {len(df)}")
    
    # Errors by level
    print("\nErrors by Level:")
    level_counts = df['level'].value_counts()
    for level, count in level_counts.items():
        percentage = 100 * count / len(df)
        print(f"{level}: {count} errors ({percentage:.2f}%)")
    
    # Errors by provider
    print("\nErrors by Provider:")
    provider_counts = df['provider'].value_counts()
    for provider, count in provider_counts.items():
        percentage = 100 * count / len(df)
        print(f"{provider}: {count} errors ({percentage:.2f}%)")
    
    # Errors by event type
    print("\nErrors by Event Type:")
    event_counts = df['event_type'].value_counts()
    for event, count in event_counts.items():
        percentage = 100 * count / len(df)
        print(f"{event}: {count} errors ({percentage:.2f}%)")
    
    # Errors by hour
    print("\nErrors by Hour of Day:")
    hour_counts = df['hour'].value_counts().sort_index()
    for hour, count in hour_counts.items():
        percentage = 100 * count / len(df)
        print(f"Hour {hour}: {count} errors ({percentage:.2f}%)")
    
    # Sample of error messages
    print("\nSample Error Messages:")
    samples = df.sample(min(5, len(df)))
    for _, row in samples.iterrows():
        print(f"[{row['timestamp']}] [{row['level']}] {row['event_type']}: {row['details']}")
    
    # Visualize errors by hour
    plt.figure(figsize=(12, 6))
    sns.countplot(x='hour', data=df)
    plt.title('Errors by Hour of Day')
    plt.xlabel('Hour of Day')
    plt.ylabel('Number of Errors')
    plt.savefig('errors_by_hour.png')

def check_for_correlations(df_deliveries):
    """Check for correlations between delivery time and other factors."""
    if df_deliveries is None or len(df_deliveries) == 0:
        return
    
    print("\n===== CORRELATION ANALYSIS =====")
    
    # Add numeric hour column for correlation analysis
    df_deliveries['hour_num'] = df_deliveries['hour']
    
    # Add numeric representation of time of day
    df_deliveries['time_num'] = df_deliveries['timestamp'].apply(
        lambda x: x.hour * 3600 + x.minute * 60 + x.second
    )
    
    # Convert phone_number to length of phone number for possible correlation
    df_deliveries['phone_length'] = df_deliveries['phone_number'].apply(lambda x: len(str(x)))
    
    # Correlation with hour of day
    hour_corr = np.corrcoef(df_deliveries['hour_num'], df_deliveries['delivery_time'])[0, 1]
    print(f"Correlation between hour of day and delivery time: {hour_corr:.4f}")
    
    # Correlation with time of day (as seconds since midnight)
    time_corr = np.corrcoef(df_deliveries['time_num'], df_deliveries['delivery_time'])[0, 1]
    print(f"Correlation between time of day and delivery time: {time_corr:.4f}")
    
    # Correlation with phone number length
    phone_corr = np.corrcoef(df_deliveries['phone_length'], df_deliveries['delivery_time'])[0, 1]
    print(f"Correlation between phone number length and delivery time: {phone_corr:.4f}")
    
    # Scatter plot of delivery time vs. hour of day
    plt.figure(figsize=(10, 6))
    sns.scatterplot(x='hour', y='delivery_time', data=df_deliveries, alpha=0.5)
    plt.title('Delivery Time vs. Hour of Day')
    plt.xlabel('Hour of Day')
    plt.ylabel('Delivery Time (seconds)')
    plt.savefig('delivery_time_vs_hour_scatter.png')
    
    # Scatter plot with regression line
    plt.figure(figsize=(10, 6))
    sns.regplot(x='hour_num', y='delivery_time', data=df_deliveries, scatter_kws={'alpha':0.3})
    plt.title('Delivery Time vs. Hour of Day with Regression Line')
    plt.xlabel('Hour of Day')
    plt.ylabel('Delivery Time (seconds)')
    plt.savefig('delivery_time_vs_hour_regplot.png')

def parse_arguments():
    """Parse command line arguments"""
    parser = argparse.ArgumentParser(description="Analyze SMS logs for delivery time patterns")
    parser.add_argument("--log-dir", default="od_logs", help="Directory containing SMS log files")
    parser.add_argument("--missing-deliveries", action="store_true", help="Analyze messages sent but missing delivery confirmation")
    parser.add_argument("--tail-percent", type=float, default=5.0, help="Percentile threshold for identifying the tail of slow deliveries (default: 5.0)")
    parser.add_argument("--output-dir", default=".", help="Directory to save output files")
    
    return parser.parse_args()

if __name__ == "__main__":
    # Parse command line arguments
    args = parse_arguments()
    
    # Use the specified logs directory
    log_dir = args.log_dir
    
    # Check if directory exists
    if not os.path.isdir(log_dir):
        print(f"Error: Log directory '{log_dir}' not found.")
        sys.exit(1)
    
    # Create output directory if it doesn't exist
    os.makedirs(args.output_dir, exist_ok=True)
    
    # Check for missing deliveries if requested
    if args.missing_deliveries:
        missing = find_missing_deliveries(log_dir)
    
    # Analyze delivery times
    df_deliveries, df_timeouts, df_errors = analyze_logs(log_dir)
    
    # Additional correlation analysis if we have delivery data
    if df_deliveries is not None and len(df_deliveries) > 0:
        check_for_correlations(df_deliveries)
    
    print("\nAnalysis complete. Visualizations saved to disk.")
    print("Detailed data available in the returned DataFrames.") 