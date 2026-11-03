# Oracle Cloud VM + k3s setup

Step-by-step guide for the production server: one Ampere A1 VM on Oracle Cloud Infrastructure (OCI) running k3s. GitHub Actions (`.github/workflows/deploy.yml`) deploys to it on every push to `main`.

Versions in this guide were checked on 2026-11-03:

| Component | Version | Source |
|---|---|---|
| k3s | `v1.37.1+k3s1` (bundles Traefik, CoreDNS, local-path storage) | https://github.com/k3s-io/k3s/releases |
| cert-manager | `v1.21.3` | https://cert-manager.io/docs/installation/kubectl/ |
| OS image | Canonical Ubuntu 24.04 (aarch64) | OCI console |

Placeholders used below:

- `<IP>`: the VM's public IPv4 address, e.g. `151.145.10.20`.
- Public URL: `https://<IP>.sslip.io`. sslip.io resolves `151.145.10.20.sslip.io` to `151.145.10.20`, so we need no domain.

Commands marked **laptop** run on your Windows machine in **Git Bash** (it includes `ssh`, `ssh-keygen` and `openssl`). Commands marked **VM** run on the server over SSH.

---

## 1. Create the VM (OCI console)

Already in progress. The settings:

| Setting | Value |
|---|---|
| Region | Italy Northwest (Milan), `eu-milan-1` |
| Shape | `VM.Standard.A1.Flex`, **2 OCPU, 12 GB memory** (Always Free covers exactly 2 OCPU / 12 GB in total for A1) |
| Image | Canonical Ubuntu 24.04, aarch64 |
| Boot volume | 100 GB (Always Free covers 200 GB of block storage in total) |
| Networking | VCN with a **public** subnet (the wizard's "Create new virtual cloud network" makes one) |
| Public IPv4 | Yes if the wizard allows it. If you created the instance with "No", see §1.2 |
| SSH key | Upload the public key `~/.ssh/beko-oci.pub` |

If you do not have the key yet (**laptop**):

```bash
ssh-keygen -t ed25519 -f ~/.ssh/beko-oci -C "beko-oci"
cat ~/.ssh/beko-oci.pub   # paste this into "Add SSH keys" -> "Paste public keys"
```

If you get **"Out of capacity"**, see [Troubleshooting → Out of capacity](#out-of-capacity-for-vmstandarda1flex).

### 1.1 Open ports 80 and 443 in the Security List

The VCN's firewall is the subnet's Security List. Port 22 is open by default. Add 80 (HTTP, also needed for the Let's Encrypt HTTP-01 challenge) and 443 (HTTPS):

1. Menu ☰ → **Networking** → **Virtual cloud networks** → your VCN.
2. Open the **Security** tab (older console: **Security Lists** in the left menu) → **Default Security List for <vcn-name>**.
3. **Security rules** tab → **Add Ingress Rules**. Add two rules (or one rule with destination port range `80,443`):

   | Field | Rule 1 | Rule 2 |
   |---|---|---|
   | Stateless | unchecked | unchecked |
   | Source type | CIDR | CIDR |
   | Source CIDR | `0.0.0.0/0` | `0.0.0.0/0` |
   | IP protocol | TCP | TCP |
   | Source port range | (empty = all) | (empty = all) |
   | Destination port range | `80` | `443` |
   | Description | HTTP | HTTPS |

4. **Add Ingress Rules**. Do **not** open 6443 (Kubernetes API); we reach it only through SSH.

### 1.2 Assign a public IP after creation (if the instance has none)

If the instance was created with **Public IPv4: No**, give it an ephemeral public IP (free, stays as long as the instance exists; it changes only if you remove it or terminate the instance):

1. **Compute** → **Instances** → your instance.
2. **Networking** tab (older console: **Attached VNICs** under Resources) → click the primary VNIC.
3. **IP administration** tab (older console: **IPv4 Addresses** under Resources).
4. On the primary private IP row, click **⋮** → **Edit**.
5. **Public IP type**: **Ephemeral public IP** → **Update**.
6. Copy the public IP shown on the row. That is `<IP>`.

If "Ephemeral public IP" is greyed out, the subnet is private. Create the instance in the VCN's **public** subnet (the one whose route table has a route `0.0.0.0/0 → Internet Gateway`).

Optional, but recommended if you may rebuild the VM: create a **Reserved public IP** (Networking → IP management → Reserved public IPs; check the price list / your tenancy limits first, it may not be covered by Always Free) and choose **Reserved public IP** in step 5. Then the IP and the URL survive a re-creation.

### 1.3 First SSH login (laptop)

```bash
chmod 600 ~/.ssh/beko-oci
ssh -i ~/.ssh/beko-oci ubuntu@<IP>
```

The default user of the Canonical Ubuntu image is `ubuntu`.

---

## 2. Prepare the OS (VM)

```bash
sudo apt-get update && sudo apt-get -y upgrade
sudo timedatectl set-timezone Europe/Istanbul
sudo reboot   # if a kernel update was installed; then ssh in again
```

### 2.1 Host firewall (iptables)

OCI's Ubuntu images ship their own iptables rules in `/etc/iptables/rules.v4` (loaded by `netfilter-persistent`). They allow only SSH and end with:

```
-A INPUT -j REJECT --reject-with icmp-host-prohibited
-A FORWARD -j REJECT --reject-with icmp-host-prohibited
```

So even with the Security List open, ports 80/443 are rejected on the VM, and the `FORWARD` reject breaks pod-to-pod and pod-to-service traffic (k3s pod CIDR `10.42.0.0/16`, service CIDR `10.43.0.0/16`). Symptoms: CoreDNS timeouts, `connection refused`/`no route to host` between pods, Traefik not reachable from outside.

Do this **before installing k3s** so the saved rule file contains only these base rules, not k3s' own chains:

```bash
# Inbound HTTP/HTTPS
sudo iptables -I INPUT 1 -p tcp -m multiport --dports 80,443 -m conntrack --ctstate NEW -j ACCEPT
# k3s pods and services (traffic coming from pods to the host, e.g. kubelet, DNS, metrics)
sudo iptables -I INPUT 1 -s 10.42.0.0/16 -j ACCEPT
sudo iptables -I INPUT 1 -s 10.43.0.0/16 -j ACCEPT
# Forwarding to and from pods (Traefik -> pods, pod -> pod, pod -> internet)
sudo iptables -I FORWARD 1 -s 10.42.0.0/16 -j ACCEPT
sudo iptables -I FORWARD 1 -d 10.42.0.0/16 -j ACCEPT
sudo iptables -I FORWARD 1 -s 10.43.0.0/16 -j ACCEPT
sudo iptables -I FORWARD 1 -d 10.43.0.0/16 -j ACCEPT

# Persist across reboots (writes /etc/iptables/rules.v4)
sudo netfilter-persistent save

# Check: the ACCEPT rules must be above the REJECT lines
sudo iptables -L INPUT -n --line-numbers
sudo iptables -L FORWARD -n --line-numbers
```

Do not run `ufw enable`; OCI images use the iptables rules above. Do not run `netfilter-persistent save` again after k3s is installed (it would save k3s' dynamic rules into the file). If you ever need to change the base rules later, edit `/etc/iptables/rules.v4` by hand and reboot.

---

## 3. Install k3s (VM)

```bash
curl -sfL https://get.k3s.io | INSTALL_K3S_VERSION="v1.37.1+k3s1" sh -s - server \
  --write-kubeconfig-mode 0600
```

This installs k3s as a systemd service with Traefik (ingress on ports 80/443 via ServiceLB), CoreDNS, metrics-server and the `local-path` storage class. The kubeconfig is written to `/etc/rancher/k3s/k3s.yaml` (root only).

kubectl for the `ubuntu` user:

```bash
mkdir -p ~/.kube
sudo cp /etc/rancher/k3s/k3s.yaml ~/.kube/config
sudo chown "$USER:$USER" ~/.kube/config
chmod 600 ~/.kube/config

kubectl get nodes            # STATUS must be Ready (about 30 s after install)
kubectl get pods -A          # coredns, local-path-provisioner, metrics-server, traefik: Running
kubectl get storageclass     # local-path (default)
```

Phase 0 check from the **laptop**: `curl -i http://<IP>.sslip.io` must answer `404 page not found` (that is Traefik; there are no routes yet).

---

## 4. Install cert-manager (VM)

```bash
kubectl apply -f https://github.com/cert-manager/cert-manager/releases/download/v1.21.3/cert-manager.yaml
kubectl -n cert-manager rollout status deploy/cert-manager-webhook --timeout=180s
kubectl get pods -n cert-manager   # cert-manager, cert-manager-cainjector, cert-manager-webhook: Running
```

The Let's Encrypt `ClusterIssuer`s (`letsencrypt-prod`, `letsencrypt-staging`) are in `deploy/kubernetes/cluster-issuer.yaml`; `deploy.yml` applies them on every deploy. Nothing else to do here.

---

## 5. Deploy user for GitHub Actions

### 5.1 Key pair (laptop)

A separate key, used only by GitHub Actions. No passphrase (Actions cannot type one).

```bash
ssh-keygen -t ed25519 -f ~/.ssh/beko-deploy -N "" -C "github-actions-deploy"
cat ~/.ssh/beko-deploy.pub    # copy this line for the next step
```

### 5.2 User on the VM (VM)

```bash
sudo adduser --disabled-password --gecos "" deploy
sudo install -d -m 700 -o deploy -g deploy /home/deploy/.ssh /home/deploy/.kube
echo "PASTE-THE-beko-deploy.pub-LINE-HERE" | sudo tee /home/deploy/.ssh/authorized_keys
sudo chown deploy:deploy /home/deploy/.ssh/authorized_keys
sudo chmod 600 /home/deploy/.ssh/authorized_keys

# kubectl access for deploy (cluster-admin kubeconfig; acceptable for this single-node demo cluster)
sudo cp /etc/rancher/k3s/k3s.yaml /home/deploy/.kube/config
sudo chown deploy:deploy /home/deploy/.kube/config
sudo chmod 600 /home/deploy/.kube/config
```

### 5.3 Test it (laptop)

```bash
ssh -i ~/.ssh/beko-deploy deploy@<IP> kubectl get nodes
```

Then record the host key for GitHub (the value of the `DEPLOY_KNOWN_HOSTS` secret):

```bash
ssh-keyscan -t ed25519 <IP>
```

---

## 6. GitHub configuration

Repository → **Settings** → **Secrets and variables** → **Actions** → **New repository secret**. Generate random values in Git Bash; keep a copy in your password manager.

| Secret | Value | How to generate |
|---|---|---|
| `DEPLOY_HOST` | `<IP>` | — |
| `DEPLOY_USER` | `deploy` | — |
| `DEPLOY_SSH_KEY` | full content of `~/.ssh/beko-deploy` (the **private** key, including the `BEGIN`/`END` lines) | `cat ~/.ssh/beko-deploy` |
| `DEPLOY_KNOWN_HOSTS` | output of `ssh-keyscan -t ed25519 <IP>` (recommended; without it the workflow trusts the key on first contact) | §5.3 |
| `ANTHROPIC_API_KEY` | Anthropic API key | console.anthropic.com |
| `JWT_SIGNING_KEY` | ≥ 32 random bytes | `openssl rand -base64 48` |
| `POSTGRES_PASSWORD` | hex only (it goes into a connection string) | `openssl rand -hex 24` |
| `MONGO_PASSWORD` | hex only (it goes into a `mongodb://` URI) | `openssl rand -hex 24` |
| `GRAFANA_ADMIN_PASSWORD` | Grafana `admin` password | `openssl rand -hex 16` |
| `GHCR_PULL_TOKEN` | optional, only if the GHCR packages stay private: a classic PAT with `read:packages` | github.com/settings/tokens |

Optional repository **variable** (Variables tab): `PUBLIC_HOST`, only if you want a host other than `<DEPLOY_HOST>.sslip.io`.

The deploy job uses the GitHub **environment** `production` (created automatically on first run). To require your approval before every deploy: Settings → Environments → production → Required reviewers.

Important: Postgres and MongoDB set their passwords only on the **first** start (empty volume). Changing `POSTGRES_PASSWORD` / `MONGO_PASSWORD` later does not change the database users; see Troubleshooting.

---

## 7. First deploy

1. The lead merges `dev` into `main` (only after your approval). Or run it by hand: Actions → **Deploy** → **Run workflow** on `main`.
2. Job **build** pushes `ghcr.io/mberke1597/beko-backend` and `beko-frontend` with tags `<git-sha>` and `latest` (linux/arm64, built on GitHub's `ubuntu-24.04-arm` runners).
3. **Make the packages public** (once, right after the first build; the repo is public anyway): github.com/mberke1597 → **Packages** → `beko-backend` → **Package settings** → **Change visibility** → Public. Same for `beko-frontend`. Without this (or the `GHCR_PULL_TOKEN` secret), pods stay in `ImagePullBackOff`; re-run the failed **deploy** job after changing visibility.
4. Job **deploy** renders `deploy/kubernetes` (kustomize + `envsubst` for `${PUBLIC_HOST}` and `${IMAGE_TAG}`), applies the ClusterIssuers, the `beko-secrets` Secret and the app over SSH, waits for `kubectl rollout status` and calls `https://<IP>.sslip.io/api/version`.

The first deploy pulls about 2 GB of images; expect 5–10 minutes.

---

## 8. Check that it works

On the VM (or `ssh -i ~/.ssh/beko-deploy deploy@<IP> ...`):

```bash
kubectl get nodes
kubectl -n beko get pods -o wide        # all Running and READY 1/1
kubectl -n beko get pvc                 # all Bound (local-path)
kubectl -n beko get ingress,certificate # certificate beko-tls READY=True
kubectl top pods -n beko                # memory per pod (metrics-server)
free -h
```

From the laptop:

```bash
curl -I http://<IP>.sslip.io            # 301 -> https
curl https://<IP>.sslip.io/health       # Healthy
curl https://<IP>.sslip.io/health/ready # Healthy (Postgres + Mongo reachable)
curl https://<IP>.sslip.io/api/version
```

Browser: `https://<IP>.sslip.io` (Beko login shell) and `https://<IP>.sslip.io/grafana/` (user `admin`, password = `GRAFANA_ADMIN_PASSWORD`).

Useful commands:

```bash
kubectl -n beko logs deploy/backend --tail=100 -f
kubectl -n beko rollout restart deploy/backend     # e.g. after changing secrets
kubectl -n beko describe pod <pod>
sudo systemctl status k3s ; sudo journalctl -u k3s -n 100
```

---

## Troubleshooting

### "Out of capacity" for VM.Standard.A1.Flex

Milan (`eu-milan-1`) has a single availability domain (AD-1), so "try another AD" is not an option. The error means Oracle has no free A1 hosts for Always Free accounts at that moment; it is temporary.

1. **Retry.** Capacity is freed all the time. Keep the create form filled in and press **Create** again every few minutes; early morning and late evening (CET) work best. Requesting a smaller shape first (1 OCPU / 6 GB) sometimes succeeds when 2/12 fails; you can resize to 2 OCPU / 12 GB later (Instance → **Edit** → **Edit shape**, needs a reboot and also needs capacity).
2. **Do not hammer with scripts.** Automated create loops against the API can get the tenancy rate-limited; manual retries a few minutes apart are fine.
3. **Upgrade to Pay As You Go (PAYG).** Oracle documents this as the fix: paid accounts get access to more capacity, and **Always Free resources stay free after the upgrade** (you pay only for usage above the Always Free limits, and our design stays inside them: 2 OCPU, 12 GB, 100 GB boot volume).
   - Menu ☰ → **Billing & Cost Management** → **Upgrade and Manage Payment** → **Upgrade to Pay As You Go**. It needs a credit card verification and can take from minutes up to a day to complete.
   - **Create a budget alert right away** so a mistake cannot cost money unnoticed: ☰ → **Billing & Cost Management** → **Budgets** → **Create Budget**:
     - Target: Compartment, your root compartment
     - Monthly budget amount: `1` (USD/EUR, the smallest useful amount)
     - Budget alert rule: Type **Actual Spend**, Threshold **Percentage of budget** `1` (or amount `0.01`), e-mail recipients: your address.
     - Add a second rule with Type **Forecast Spend**, 100 %.
   - Optional hard guard: ☰ → **Governance & Administration** → **Quota Policies** → add a compartment quota that limits A1 cores to 2 and A1 memory to 12 GB (Oracle recommends compartment quotas for exactly this).
   - Then retry the create; PAYG requests usually succeed within minutes.
4. **Create without a public IP if the wizard insists, then add it later.** Creating the instance with **Public IPv4: No** is fine: after it is running, assign an ephemeral (or reserved) public IP as in §1.2.
5. Only as a last resort: another region. The home region cannot be changed and Always Free A1 is only available in the home region, so this would mean a new tenancy.

### Ports 80/443 not reachable

Check in this order:

1. Security List has the ingress rules (§1.1) on the subnet the VM is in.
2. VM iptables (§2.1): `sudo iptables -L INPUT -n --line-numbers`. The `ACCEPT` for 80/443 and for `10.42.0.0/16` / `10.43.0.0/16` must be above `REJECT ... icmp-host-prohibited`. Same for `FORWARD`.
3. Traefik is running: `kubectl -n kube-system get pods,svc | grep traefik`. The `traefik` LoadBalancer service must show the VM's private IP as EXTERNAL-IP and ports 80/443.
4. Locally on the VM: `curl -i http://localhost` → Traefik 404 or a redirect.

### Pods cannot resolve DNS or reach each other

Almost always the OCI `FORWARD ... REJECT` rule. Re-check the FORWARD rules from §2.1, then `kubectl -n kube-system rollout restart deploy/coredns`. Test: `kubectl run -it --rm dnstest --image=busybox:1.37 --restart=Never -- nslookup backend.beko.svc.cluster.local`.

### Certificate stays pending (`READY=False`)

```bash
kubectl -n beko get certificate,certificaterequest,order,challenge
kubectl -n beko describe challenge
kubectl -n cert-manager logs deploy/cert-manager --tail=100
```

- Challenge `pending` with a connection error: Let's Encrypt cannot reach `http://<IP>.sslip.io/.well-known/acme-challenge/...` on port **80**. Fix the Security List / iptables (above). Test from the laptop: `curl -i http://<IP>.sslip.io/.well-known/acme-challenge/test` should return 404 from Traefik, not time out.
- DNS: `nslookup <IP>.sslip.io` must return `<IP>`.
- `429` / `rateLimited`: Let's Encrypt rate limits (sslip.io is shared by many users). Switch temporarily to staging: change the annotation `cert-manager.io/cluster-issuer` in `deploy/kubernetes/ingress.yaml` to `letsencrypt-staging` (browser warning, but proves the setup), or wait for the limit window to pass.
- After fixing the cause, delete the stuck request so it retries immediately: `kubectl -n beko delete certificaterequest --all`.

Until the certificate is issued Traefik serves its self-signed default certificate (browser warning).

### `ImagePullBackOff`

The GHCR packages are private. Make them public (§7 step 3) or set the `GHCR_PULL_TOKEN` secret, then re-run the deploy job. Check the tag exists: `docker manifest inspect ghcr.io/mberke1597/beko-backend:<sha>`.

### Backend not ready (`0/1`, readiness `/health/ready` fails)

`kubectl -n beko logs deploy/backend`. Usually Postgres/Mongo authentication: the DB passwords in `beko-secrets` must match what the databases were initialised with. If you changed them after the first start, either set them back, or (data loss!) delete the volumes so the databases re-initialise: `kubectl -n beko delete statefulset postgres mongodb && kubectl -n beko delete pvc data-postgres-0 data-mongodb-0`, then re-run the deploy.

### Pods `Pending` / `OOMKilled`

`kubectl describe pod <pod>` shows the reason. Requests/limits follow Planning/Proposal.md §5 (total limits ≈ 6.8 GB). Check `free -h` and `kubectl top pods -A`.

### GitHub Actions cannot SSH

- `Permission denied (publickey)`: `DEPLOY_SSH_KEY` must be the private key with its first and last lines, and the matching `.pub` must be in `/home/deploy/.ssh/authorized_keys` (mode 600, owner deploy).
- `Host key verification failed`: `DEPLOY_KNOWN_HOSTS` is stale (VM re-created). Run `ssh-keyscan -t ed25519 <IP>` again and update the secret.
- Timeout: port 22 in the Security List and iptables, correct `DEPLOY_HOST`.

### Rebuilding the VM from scratch

Repeat §2–§5 on the new VM, update `DEPLOY_HOST` and `DEPLOY_KNOWN_HOSTS` (and nothing else if you used a reserved IP), then re-run the deploy workflow.
