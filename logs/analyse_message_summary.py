import pandas as pd
import datetime

# --- Data Loading ---
def load_data(path="message_summary.csv") -> pd.DataFrame:
    df = pd.read_csv(path)
    # Parse timestamps as UTC then convert to Pacific/Auckland (handles DST)
    df["FirstLogTime"] = (
        pd.to_datetime(df["FirstLogTime"], errors="raise", utc=True)
          .dt.tz_convert("Pacific/Auckland")
    )
    return df.sort_values("FirstLogTime").reset_index(drop=True)

# --- Outcome Summary ---
def print_outcome_summary(df: pd.DataFrame) -> None:
    print("\n== Outcome Counts & Percentages ==")
    counts = df["UltimateResult"].value_counts().sort_index()
    total = counts.sum()
    for outcome, count in counts.items():
        pct = (count / total) * 100
        print(f"{outcome:15s}: {count:5d} ({pct:6.2f}%)")

# --- Cluster Analysis ---
def classify_type(outcome: str) -> str:
    return "Sent" if outcome in ("Delivered", "Failed") else "Gave up trying"

def compute_clusters(df: pd.DataFrame) -> pd.DataFrame:
    clusters = []
    current_type = None
    buffer = []
    for _, row in df.iterrows():
        t = classify_type(row["UltimateResult"])
        if current_type is None or t == current_type:
            buffer.append(row)
            current_type = t
        else:
            sub = pd.DataFrame(buffer)
            clusters.append({
                "Type": current_type,
                "Size": len(sub),
                "AvgDuration_sec": sub["DurationSeconds"].mean(),
                "StartTime": sub["FirstLogTime"].iloc[0],
                "EndTime": sub["FirstLogTime"].iloc[-1]
            })
            buffer = [row]
            current_type = t
    if buffer:
        sub = pd.DataFrame(buffer)
        clusters.append({
            "Type": current_type,
            "Size": len(sub),
            "AvgDuration_sec": sub["DurationSeconds"].mean(),
            "StartTime": sub["FirstLogTime"].iloc[0],
            "EndTime": sub["FirstLogTime"].iloc[-1]
        })
    out = pd.DataFrame(clusters)
    out["StartTime"] = out["StartTime"].dt.strftime("%Y-%m-%d %H:%M:%S%z")
    out["EndTime"] = out["EndTime"].dt.strftime("%Y-%m-%d %H:%M:%S%z")
    return out

# --- Gave Up Context ---
def analyse_gave_up_context(df: pd.DataFrame) -> pd.DataFrame:
    rows = []
    for i, row in df.iterrows():
        if row["UltimateResult"] != "Gave up trying":
            continue
        prev_ = df.iloc[i-1]["UltimateResult"] if i > 0 else None
        next_ = df.iloc[i+1]["UltimateResult"] if i < len(df)-1 else None
        rows.append({
            "FailureTime": row["FirstLogTime"],
            "PreviousResult": prev_,
            "NextResult": next_
        })
    return pd.DataFrame(rows)

def print_gave_up_context_stats(ctx: pd.DataFrame) -> None:
    inside = ((ctx["PreviousResult"] == "Gave up trying") & (ctx["NextResult"] == "Gave up trying")).sum()
    starts = ((ctx["PreviousResult"] != "Gave up trying") & (ctx["NextResult"] == "Gave up trying")).sum()
    ends = ((ctx["PreviousResult"] == "Gave up trying") & (ctx["NextResult"] != "Gave up trying")).sum()
    isolated = ((ctx["PreviousResult"] != "Gave up trying") & (ctx["NextResult"] != "Gave up trying")).sum()
    print("\n== Gave Up Trying Context ==")
    print(f"Inside block : {inside}")
    print(f"Starts block : {starts}")
    print(f"Ends block   : {ends}")
    print(f"Isolated     : {isolated}")

# --- Reminder Classification ---
def classify_reminder_message(msg: str) -> str:
    if not isinstance(msg, str):
        return "unknown"
    if "Happy Birthday" in msg:
        return "birthday"
    elif "2 WEEKS" in msg:
        return "2 week"
    elif "NEXT WEEK" in msg:
        return "1 week"
    elif "Your dental appointment is on" in msg:
        return "next day"
    else:
        return "unknown"

# --- Daily Reminder Summary ---
def daily_reminder_type_summary(df: pd.DataFrame) -> pd.DataFrame:
    # Prepare a copy
    df2 = df.copy()
    # Extract date and weekday
    df2["Date"] = df2["FirstLogTime"].dt.date
    df2["Weekday"] = df2["FirstLogTime"].dt.day_name()
    # Filter 08:15–08:30 NZT window
    mask = (
        (df2["FirstLogTime"].dt.time >= datetime.time(8, 15)) &
        (df2["FirstLogTime"].dt.time <= datetime.time(8, 30))
    )
    morning = df2.loc[mask].copy()
    # Classify
    morning["Type"] = morning["Message"].apply(classify_reminder_message)
    # Pivot
    categories = ["2 week", "1 week", "next day", "birthday", "unknown"]
    summary = (
        morning
        .groupby(["Date", "Weekday", "Type"]).size()
        .unstack(fill_value=0)
    )
    for col in categories:
        if col not in summary.columns:
            summary[col] = 0
    summary = summary.reset_index()[["Date", "Weekday"] + categories]
    # Include all calendar days
    all_dates = pd.date_range(
        start=df2["Date"].min(),
        end=df2["Date"].max(),
        freq="D"
    ).date
    all_df = pd.DataFrame({"Date": all_dates})
    all_df["Weekday"] = pd.to_datetime(all_df["Date"]).dt.day_name()
    # Merge to fill missing days
    result = pd.merge(all_df, summary, on=["Date","Weekday"], how="left").fillna(0)
    result[categories] = result[categories].astype(int)
    # Add ProblemDay: True if all non-unknown counts are 0
    result["ProblemDay"] = (
        result[["2 week", "1 week", "next day", "birthday"]].sum(axis=1) == 0
    )
    # Print & save
    print("\n== Daily 08:15–08:30 NZT Summary (All Days) ==")
    print(result.to_string(index=False))
    result.to_csv("daily_reminder_summary_all_days_with_weekday.csv", index=False)
    print("Saved daily_reminder_summary_all_days_with_weekday.csv")
    return result

# --- Main ---
def main():
    df = load_data()
    print_outcome_summary(df)
    ctx = analyse_gave_up_context(df)
    print_gave_up_context_stats(ctx)
    clust = compute_clusters(df)
    print("\n== Cluster Analysis ==")
    print(clust.to_string(index=False))
    daily_reminder_type_summary(df)

if __name__ == "__main__":
    main()
