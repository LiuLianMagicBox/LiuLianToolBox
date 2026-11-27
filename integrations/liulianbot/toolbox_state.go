// These files are compiled inside LiuLianBot's existing bot package.
package bot

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"slices"
	"strings"
	"sync"
	"time"
	"unicode/utf8"
)

type tbError string

func (e tbError) Error() string { return string(e) }

type tbApp struct{ ID, Name string }

type tbVoice struct {
	GuildID, ChannelID string
	GuildName          string `json:",omitempty"`
	ChannelName        string `json:",omitempty"`
}

type tbAlarm struct {
	ID  string
	Due time.Time
}

type tbIdle struct {
	Enabled bool
	Minutes int
	Reply   string
}

type tbMailbox struct {
	Enabled bool
	Route   string
}

type tbUser struct {
	DefaultDevice string
	Idle          tbIdle
	Mailbox       tbMailbox
}

type tbDevice struct {
	ID, Name, Owner, TokenHash, Peer, Presenter string
	Alarms                                      []tbAlarm
}

type tbReceipt struct {
	Owner, Sender, Route, Status, ProofHash, BodyHash string
	At                                                time.Time
}

type tbData struct {
	Devices  map[string]*tbDevice
	Users    map[string]*tbUser
	Receipts map[string]*tbReceipt
}

func (d *tbData) user(id string) *tbUser {
	if d.Users[id] == nil {
		d.Users[id] = &tbUser{Idle: tbIdle{Minutes: 10}, Mailbox: tbMailbox{Route: "dm"}}
	}
	return d.Users[id]
}

type tbGrant struct {
	Idle, Mailbox bool
	Email         string
}

type tbConfig struct {
	Listen, StateFile string
	Users             map[string]tbGrant
	SMTP              struct {
		Host, From, Username, RelayInbox string
		Port                             int
	}
}

type tbLive struct {
	Seen        time.Time
	IdleSeconds int64
	Apps        []tbApp
}

type tbCode struct {
	Subject string
	Until   time.Time
}

type tbLaunch struct{ ID, From, Target, App string }

type tbWaiting struct {
	tbLaunch
	Done    chan string
	Offered bool
}

type tbPrivate struct {
	GuildID, ChannelID, Name string
	Limit                    int
}

type tbBridge struct {
	mu                            sync.Mutex
	data                          tbData
	configPath, statePath, listen string
	live                          map[string]tbLive
	links, invites                map[string]tbCode
	jobs                          map[string]*tbWaiting
	now                           func() time.Time
	voices                        func(...string) map[string]*tbVoice
	channels                      func(context.Context, string) ([]tbPrivate, error)
	edit                          func(context.Context, string, tbPrivate) error
	move                          func(context.Context, string, tbRequest) error
	deliverDM                     func(context.Context, string, string, string) error
	sendMail                      func(context.Context, tbConfig, tbEnvelope) error
	mailMu                        sync.Mutex
}

func tbRandom() string {
	var entropy [24]byte
	rand.Read(entropy[:])
	return hex.EncodeToString(entropy[:])
}

func tbHash(s string) string { h := sha256.Sum256([]byte(s)); return hex.EncodeToString(h[:]) }

func tbText(s string, max int) bool {
	return strings.TrimSpace(s) != "" && utf8.RuneCountInString(s) <= max
}

func tbSettings(path string) (tbConfig, error) {
	var c tbConfig
	raw, err := os.ReadFile(path)
	if err == nil {
		err = json.Unmarshal(raw, &c)
	}
	return c, err
}

func newTB(path string) (*tbBridge, error) {
	c, err := tbSettings(path)
	if err != nil {
		return nil, err
	}
	host, _, err := net.SplitHostPort(c.Listen)
	if err != nil || net.ParseIP(host) == nil || !net.ParseIP(host).IsLoopback() || c.StateFile == "" {
		return nil, errors.New("ToolBox requires a loopback listen address and a state file")
	}
	statePath := c.StateFile
	if !filepath.IsAbs(statePath) {
		statePath = filepath.Join(filepath.Dir(path), statePath)
	}
	s := &tbBridge{configPath: path, statePath: statePath, listen: c.Listen, now: time.Now,
		data: tbData{map[string]*tbDevice{}, map[string]*tbUser{}, map[string]*tbReceipt{}},
		live: map[string]tbLive{}, links: map[string]tbCode{}, invites: map[string]tbCode{}, jobs: map[string]*tbWaiting{}}
	raw, err := os.ReadFile(statePath)
	if err == nil {
		err = json.Unmarshal(raw, &s.data)
	} else if errors.Is(err, os.ErrNotExist) {
		err = nil
	}
	if err != nil || s.data.Devices == nil || s.data.Users == nil || s.data.Receipts == nil {
		return nil, errors.New("cannot load ToolBox state")
	}
	s.sendMail = tbSMTP
	return s, nil
}

// All mutations run under mu. Roll back the in-memory state on a disk failure.
func (s *tbBridge) change(fn func()) error {
	previous, err := json.Marshal(s.data)
	if err != nil {
		return err
	}
	fn()
	err = s.persist()
	if err != nil {
		var old tbData
		if json.Unmarshal(previous, &old) == nil {
			s.data = old
		}
	}
	return err
}

func (s *tbBridge) persist() error {
	dir := filepath.Dir(s.statePath)
	if err := os.MkdirAll(dir, 0700); err != nil {
		return err
	}
	f, err := os.CreateTemp(dir, ".toolbox-")
	if err != nil {
		return err
	}
	defer os.Remove(f.Name())
	err = json.NewEncoder(f).Encode(s.data)
	if err == nil {
		err = f.Sync()
	}
	closeErr := f.Close()
	if err == nil {
		err = closeErr
	}
	if err == nil {
		err = os.Rename(f.Name(), s.statePath)
	}
	return err
}

const tbFresh = 15 * time.Second

func (s *tbBridge) online(id string) bool {
	l, ok := s.live[id]
	return ok && s.now().Sub(l.Seen) < tbFresh
}

func (s *tbBridge) isIdle(owner string, pref tbIdle) bool {
	if !pref.Enabled || pref.Reply == "" {
		return false
	}
	seen := false
	for id, d := range s.data.Devices {
		if d.Owner != owner || !s.online(id) {
			continue
		}
		seen = true
		if s.live[id].IdleSeconds < int64(pref.Minutes)*60 {
			return false
		}
	}
	return seen
}

// Issue one short-lived invitation per subject. Only a hash stays in memory.
func (s *tbBridge) issue(codes map[string]tbCode, subject string) string {
	for k, v := range codes {
		if v.Subject == subject || !s.now().Before(v.Until) {
			delete(codes, k)
		}
	}
	code := tbRandom()
	codes[tbHash(code)] = tbCode{subject, s.now().Add(5 * time.Minute)}
	return code
}

func (s *tbBridge) code(codes map[string]tbCode, code string) (string, error) {
	v, ok := codes[tbHash(code)]
	if !ok || !s.now().Before(v.Until) {
		return "", tbError("InvalidCode")
	}
	return v.Subject, nil
}

func (s *tbBridge) enroll(code, name string) (any, error) {
	if !tbText(name, 64) {
		return nil, tbError("InvalidInput")
	}
	owner, err := s.code(s.links, code)
	if err != nil {
		return nil, err
	}
	id, token := tbRandom()[:16], tbRandom()
	err = s.change(func() {
		s.data.Devices[id] = &tbDevice{ID: id, Owner: owner, Name: name, TokenHash: tbHash(token)}
		s.data.user(owner)
	})
	if err != nil {
		return nil, err
	}
	delete(s.links, tbHash(code))
	return map[string]string{"id": id, "token": token}, nil
}

func (s *tbBridge) authenticate(r *http.Request) *tbDevice {
	value := r.Header.Get("Authorization")
	token, ok := strings.CutPrefix(value, "Bearer ")
	if !ok || len(token) != 48 {
		return nil
	}
	hash := tbHash(token)
	for _, d := range s.data.Devices {
		if d.TokenHash == hash {
			return d
		}
	}
	return nil
}

func (s *tbBridge) clearPair(d *tbDevice) {
	if peer := s.data.Devices[d.Peer]; peer != nil && peer.Peer == d.ID {
		peer.Peer = ""
		peer.Presenter = ""
	}
	d.Peer = ""
	d.Presenter = ""
}

func (s *tbBridge) stopJobs(id string) {
	for _, j := range s.jobs {
		if j.From == id || j.Target == id {
			delete(s.jobs, j.ID)
			code := "Offline"
			if j.Offered {
				code = "Uncertain"
			}
			select {
			case j.Done <- code:
			default:
			}
		}
	}
}

func (s *tbBridge) revoke(d *tbDevice) error {
	err := s.change(func() {
		s.clearPair(d)
		delete(s.data.Devices, d.ID)
		if u := s.data.user(d.Owner); u.DefaultDevice == d.ID {
			u.DefaultDevice = ""
		}
	})
	if err == nil {
		delete(s.live, d.ID)
		s.stopJobs(d.ID)
	}
	return err
}

type tbSummary struct{ ID, Name string }

type tbPeer struct {
	ID, Name            string
	Online, SameAccount bool
	Voice               *tbVoice
	Apps                []tbApp
}

type tbSnapshot struct {
	ID, Name                 string
	Voice                    *tbVoice
	Peer                     *tbPeer
	Presenter, DefaultDevice string
	Devices                  []tbSummary
	Idle                     *tbIdle
	Mailbox                  *tbMailbox
	Alarms                   []tbAlarm
	Launches                 []tbLaunch
}

func (s *tbBridge) snapshot(d *tbDevice, grant tbGrant) tbSnapshot {
	u := s.data.user(d.Owner)
	out := tbSnapshot{ID: d.ID, Name: d.Name, Presenter: d.Presenter, DefaultDevice: u.DefaultDevice,
		Devices: []tbSummary{}, Alarms: append([]tbAlarm{}, d.Alarms...), Launches: []tbLaunch{}}
	if grant.Idle {
		v := u.Idle
		out.Idle = &v
	}
	if grant.Mailbox {
		v := u.Mailbox
		out.Mailbox = &v
	}
	for _, v := range s.data.Devices {
		if v.Owner == d.Owner {
			out.Devices = append(out.Devices, tbSummary{v.ID, v.Name})
		}
	}
	slices.SortFunc(out.Devices, func(a, b tbSummary) int {
		if c := strings.Compare(a.Name, b.Name); c != 0 {
			return c
		}
		return strings.Compare(a.ID, b.ID)
	})
	p := s.data.Devices[d.Peer]
	if p == nil || p.Peer != d.ID {
		out.Voice = s.voices(d.Owner)[d.Owner]
		return out
	}
	voices := s.voices(d.Owner, p.Owner)
	out.Voice = voices[d.Owner]
	out.Peer = &tbPeer{ID: p.ID, Name: p.Name, Online: s.online(p.ID), SameAccount: p.Owner == d.Owner, Apps: []tbApp{}}
	if out.Peer.Online {
		out.Peer.Voice = voices[p.Owner]
		out.Peer.Apps = append([]tbApp{}, s.live[p.ID].Apps...)
	}
	for _, j := range s.jobs {
		if j.Target == d.ID && j.From == p.ID {
			out.Launches = append(out.Launches, j.tbLaunch)
			j.Offered = true
		}
	}
	return out
}

func (s *tbBridge) alarm(owner, target string, due time.Time) (*tbDevice, error) {
	if !due.After(s.now()) {
		return nil, tbError("InvalidTime")
	}
	if u := s.data.Users[owner]; target == "" && u != nil {
		target = u.DefaultDevice
	}
	if target == "" {
		return nil, tbError("ChooseDevice")
	}
	d := s.data.Devices[target]
	if d == nil || d.Owner != owner {
		return nil, tbError("ChooseDevice")
	}
	if !s.online(d.ID) {
		return d, tbError("Offline")
	}
	if len(d.Alarms) >= 128 {
		return d, tbError("TooMany")
	}
	err := s.change(func() { d.Alarms = append(d.Alarms, tbAlarm{tbRandom(), due.UTC()}) })
	return d, err
}
