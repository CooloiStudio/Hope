package task

import "testing"

func TestNormalizeCountdownEditMode(t *testing.T) {
	cases := []struct {
		in, want string
	}{
		{"", CountdownEditDuration},
		{"duration", CountdownEditDuration},
		{"Duration", CountdownEditDuration},
		{"deadline", CountdownEditDeadline},
		{"DEADLINE", CountdownEditDeadline},
		{"bogus", CountdownEditDuration},
	}
	for _, c := range cases {
		if got := NormalizeCountdownEditMode(c.in); got != c.want {
			t.Errorf("NormalizeCountdownEditMode(%q)=%q, want %q", c.in, got, c.want)
		}
	}
}
