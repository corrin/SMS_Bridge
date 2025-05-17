import pandas as pd
from datetime import timedelta

def load_data(path="message_summary.csv") -> pd.DataFrame:
    df = pd.read_csv(path)
    df["FirstLogTime"] = pd.to_datetime(df["FirstLogTime"], utc=True, errors="raise")
    return df.sort_values("FirstLogTime").reset_index(drop=True)

def print_outcome_summary(df: pd.DataFrame) -> None:
    print("\n== Outcome Counts & Percentages ==")
    outcome_counts = df["UltimateResult"].value_counts().sort_index()
    total = outcome_counts.sum()
    for outcome, count in outcome_counts.items():
        pct = (count / total) * 100
        print(f"{outcome:15}: {count:5} ({pct:.2f}%)")

def classify_type(outcome: str) -> str:
    return "Sent" if outcome in ("Delivered", "Failed") else "Gave up trying"

def compute_clusters(df: pd.DataFrame) -> pd.DataFrame:
    clusters = []
    current_type = None
    current_cluster = []

    for _, row in df.iterrows():
        this_type = classify_type(row["UltimateResult"])
        if current_type is None:
            current_type = this_type
        if this_type == current_type:
            current_cluster.append(row)
        else:
            cluster_df = pd.DataFrame(current_cluster)
            clusters.append({
                "Type": current_type,
                "Size": len(cluster_df),
                "AvgDuration_sec": cluster_df["DurationSeconds"].mean(),
                "StartTime": cluster_df["FirstLogTime"].iloc[0],
                "EndTime": cluster_df["FirstLogTime"].iloc[-1]
            })
            current_type = this_type
            current_cluster = [row]

    if current_cluster:
        cluster_df = pd.DataFrame(current_cluster)
        clusters.append({
            "Type": current_type,
            "Size": len(cluster_df),
            "AvgDuration_sec": cluster_df["DurationSeconds"].mean(),
            "StartTime": cluster_df["FirstLogTime"].iloc[0],
            "EndTime": cluster_df["FirstLogTime"].iloc[-1]
        })

    result = pd.DataFrame(clusters)
    result["StartTime"] = result["StartTime"].apply(lambda dt: dt.strftime("%Y-%m-%d %H:%M:%S"))
    result["EndTime"]   = result["EndTime"].apply(lambda dt: dt.strftime("%Y-%m-%d %H:%M:%S"))
    return result

def analyse_gave_up_context(df: pd.DataFrame) -> pd.DataFrame:
    rows = []
    for i, row in df.iterrows():
        if row["UltimateResult"] != "Gave up trying":
            continue
        prev_result = df.iloc[i - 1]["UltimateResult"] if i > 0 else None
        next_result = df.iloc[i + 1]["UltimateResult"] if i < len(df) - 1 else None
        rows.append({
            "FailureTime": row["FirstLogTime"],
            "Result": row["UltimateResult"],
            "PreviousResult": prev_result,
            "NextResult": next_result,
        })
    return pd.DataFrame(rows)

def print_gave_up_context_stats(context_df: pd.DataFrame) -> None:
    inside_block = ((context_df["PreviousResult"] == "Gave up trying") & (context_df["NextResult"] == "Gave up trying")).sum()
    starts_block = ((context_df["PreviousResult"] != "Gave up trying") & (context_df["NextResult"] == "Gave up trying")).sum()
    ends_block = ((context_df["PreviousResult"] == "Gave up trying") & (context_df["NextResult"] != "Gave up trying")).sum()
    isolated = ((context_df["PreviousResult"] != "Gave up trying") & (context_df["NextResult"] != "Gave up trying")).sum()

    print("\n== Gave Up Trying Context ==")
    print(f"Surrounded by other 'Gave up trying'      : {inside_block}")
    print(f"Starts a 'Gave up trying' streak          : {starts_block}")
    print(f"Ends a 'Gave up trying' streak            : {ends_block}")
    print(f"Isolated 'Gave up trying' message         : {isolated}")

def classify_reminder_message(msg: str) -> str:
    msg = msg if isinstance(msg, str) else ""
    if "Happy Birthday" in msg:
        return "birthday"
    elif "TWO WEEKS" in msg:
        return "2 week"
    elif "NEXT WEEK" in msg:
        return "1 week"
    elif "Your dental appointment is on" in msg:
        return "next day"
    else:
        return "unknown"

def daily_reminder_type_summary(df: pd.DataFrame) -> None:
    df = df.copy()
    df["LocalTime"] = df["FirstLogTime"]
    df["Date"] = df["LocalTime"].dt.date
    df = df[
        (df["FirstLogTime"].dt.hour == 8) &
        (df["FirstLogTime"].dt.minute >= 15) &
        (df["FirstLogTime"].dt.minute < 30)
    ]

    df["ReminderType"] = df["Message"].apply(classify_reminder_message)
    summary_df = df.groupby(["Date", "ReminderType"]).size().unstack(fill_value=0).reset_index()

    for col in ["2 week", "1 week", "next day", "birthday", "unknown"]:
        if col not in summary_df.columns:
            summary_df[col] = 0

    summary_df = summary_df[["Date", "2 week", "1 week", "next day", "birthday", "unknown"]]
    print("== Daily Reminder Summary ==")
    print(summary_df.to_string(index=False))
    summary_df.to_csv("daily_reminder_summary.csv", index=False)
    print("Saved daily_reminder_summary.csv")

def save_csv(df: pd.DataFrame, filename: str) -> None:
    df.to_csv(filename, index=False)
    print(f"Saved {filename}")

def main():
    df = load_data("message_summary.csv")
    print_outcome_summary(df)

    context_df = analyse_gave_up_context(df)
    print_gave_up_context_stats(context_df)
    save_csv(context_df, "failure_sequence_context.csv")

    cluster_df = compute_clusters(df)
    print("\n== Cluster Analysis ==")
    print(cluster_df)
    save_csv(cluster_df, "cluster_summary.csv")

    daily_reminder_type_summary(df)

if __name__ == "__main__":
    main()
